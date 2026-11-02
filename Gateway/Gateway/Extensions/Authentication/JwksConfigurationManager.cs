using Dprental.Gateway.Models.Authentication;
using Dprental.Gateway.Models.Errors;
using Dprental.Gateway.Extensions.Errors;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.Gateway.Extensions.Authentication;

/// <summary>
/// Tải JWKS trực tiếp từ AuthService và chuyển thành cấu hình khóa cho bộ xác thực JWT Bearer.
/// Giữ bộ khóa hợp lệ gần nhất trong thời hạn cho phép khi AuthService tạm thời không khả dụng.
/// </summary>
internal sealed class JwksConfigurationManager(
    HttpClient httpClient,
    GatewayJwtOptions options,
    TimeProvider timeProvider) : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumUsableAge = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly Uri jwksUri = new(options.JwksUri, UriKind.Absolute);
    private sealed record KeyCache(OpenIdConnectConfiguration Configuration, DateTimeOffset FetchedAtUtc, DateTimeOffset RefreshAfterUtc);
    private KeyCache? cache;
    private int refreshRequested;
    private long nextForcedRefreshTicks;
    private DateTimeOffset retryAfterUtc;

    /// <summary>Trả bộ khóa trong cache hoặc tải lại khi đến hạn; không dùng khóa quá tuổi cho phép.</summary>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        // Đọc snapshot nguyên tử để request đồng thời có thể dùng cache mà không phải chờ khóa.
        var cachedConfiguration = Volatile.Read(ref cache);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cachedConfiguration is not null
            && Volatile.Read(ref refreshRequested) == 0
            && now < cachedConfiguration.RefreshAfterUtc
            && now - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge)
        {
            return cachedConfiguration.Configuration;
        }

        // Chỉ một request tải JWKS tại một thời điểm; truyền tín hiệu hủy khi đang chờ.
        await refreshLock.WaitAsync(cancel);
        try
        {
            // Kiểm tra lại sau khi lấy khóa: request trước có thể đã làm mới cache trong lúc chờ.
            cachedConfiguration = cache;
            now = timeProvider.GetUtcNow();
            if (cachedConfiguration is not null
                && Volatile.Read(ref refreshRequested) == 0
                && now < cachedConfiguration.RefreshAfterUtc
            && now - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge)
            {
                return cachedConfiguration.Configuration;
            }

            // Tạm ngừng gọi AuthService sau lần tải lỗi không có cache dùng được.
            if (now < retryAfterUtc)
                throw new InvalidOperationException("JWKS refresh is temporarily unavailable.");

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(jwksUri, cancel);
                response.EnsureSuccessStatusCode();
                string document = await response.Content.ReadAsStringAsync(cancel);
                JsonWebKeySet keySet = new(document);
                if (keySet.Keys.Count == 0)
                {
                    throw new InvalidOperationException("The AuthService JWKS document contains no signing keys.");
                }

                OpenIdConnectConfiguration configuration = new();
                foreach (JsonWebKey key in keySet.Keys)
                {
                    configuration.SigningKeys.Add(key);
                }

                DateTimeOffset refreshedAtUtc = timeProvider.GetUtcNow();
                // Chỉ thay cache sau khi tải và đọc được bộ khóa không rỗng.
                Volatile.Write(ref cache, new KeyCache(configuration, refreshedAtUtc,
                    refreshedAtUtc.Add(GetRefreshInterval(response.Headers.CacheControl))));
                Volatile.Write(ref refreshRequested, 0);
                return configuration;
            }
            // Khi tải lỗi, chỉ dùng lại cache nếu chưa quá 10 phút và request chưa bị hủy.
            // Lùi lần thử tiếp theo 10 giây; không kéo dài tuổi gốc FetchedAtUtc của bộ khóa.
            catch when (!cancel.IsCancellationRequested && cachedConfiguration is not null &&
                timeProvider.GetUtcNow() - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge)
            {
                Volatile.Write(ref cache, cachedConfiguration with { RefreshAfterUtc = timeProvider.GetUtcNow().AddSeconds(10) });
                Volatile.Write(ref refreshRequested, 0);
                return cachedConfiguration.Configuration;
            }
            catch
            {
                if (cancel.IsCancellationRequested) throw;
                retryAfterUtc = timeProvider.GetUtcNow().AddSeconds(10);
                throw;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>Yêu cầu tải lại khóa, có giới hạn tần suất, khi gặp mã khóa ký chưa biết.</summary>
    public void RequestRefresh()
    {
        // CompareExchange bảo đảm nhiều request gặp kid lạ chỉ đặt một yêu cầu refresh mỗi 10 giây.
        long now = timeProvider.GetUtcNow().UtcTicks;
        long next = Interlocked.Read(ref nextForcedRefreshTicks);
        if (now >= next && Interlocked.CompareExchange(ref nextForcedRefreshTicks,
            now + TimeSpan.FromSeconds(10).Ticks, next) == next)
            Volatile.Write(ref refreshRequested, 1);
    }

    /// <summary>Tìm kid trong cache, yêu cầu làm mới khi thiếu rồi kiểm tra lại bộ khóa nhận được.</summary>
    internal async Task<bool> EnsureSigningKeyAsync(string? kid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kid)) return false;
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.SigningKeys.Any(k => k.KeyId == kid)) return true;
        RequestRefresh();
        configuration = await GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys.Any(k => k.KeyId == kid);
    }

    /// <summary>Kiểm tra tuổi bộ khóa trong cache để xác định Gateway có sẵn sàng nhận request hay không.</summary>
    internal bool HasUsableConfiguration()
    {
        var cachedConfiguration = Volatile.Read(ref cache);
        return cachedConfiguration is not null
            && timeProvider.GetUtcNow() - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge;
    }

    /// <summary>Tải khóa lần đầu hoặc làm mới cache quá hạn phục vụ kiểm tra readiness.</summary>
    internal async Task EnsureUsableConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!HasUsableConfiguration())
        {
            RequestRefresh();
            await GetConfigurationAsync(cancellationToken);
        }
    }

    /// <summary>Giải phóng semaphore do đối tượng singleton này sở hữu.</summary>
    public void Dispose() => refreshLock.Dispose();

    private static TimeSpan GetRefreshInterval(CacheControlHeaderValue? cacheControl)
    {
        // Tôn trọng Max-Age nhưng giới hạn tối đa 5 phút; giá trị không dương dùng thời gian mặc định.
        TimeSpan requestedInterval = cacheControl?.MaxAge ?? DefaultRefreshInterval;
        return requestedInterval <= TimeSpan.Zero
            ? DefaultRefreshInterval
            : requestedInterval > MaximumRefreshInterval
                ? MaximumRefreshInterval
                : requestedInterval;
    }
}
