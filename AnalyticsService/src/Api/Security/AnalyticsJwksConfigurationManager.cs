using System.Net.Http.Headers;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AnalyticsService.Api.Security;

/// <summary>
/// Tải public key từ JWKS endpoint của AuthService, có cache, timeout và giới hạn độ cũ (MaxStale).
/// Cùng thiết kế với PaymentJwksConfigurationManager: AuthService chập chờn thì vẫn dùng key đã cache
/// trong phạm vi MaxStale, không làm hỏng xác thực ngay lập tức.
/// </summary>
public sealed class AnalyticsJwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AnalyticsJwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Uri _jwksUri;

    private sealed record KeyCache(OpenIdConnectConfiguration Configuration, DateTimeOffset FetchedAtUtc, DateTimeOffset RefreshAfterUtc);

    private KeyCache? _cache;
    private int _refreshRequested;
    private long _nextForcedRefreshTicks;
    private DateTimeOffset _retryAfterUtc;

    public AnalyticsJwksConfigurationManager(HttpClient httpClient, AnalyticsJwtOptions options, TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jwksUri = new Uri(_options.JwksUri, UriKind.Absolute);
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        KeyCache? cached = Volatile.Read(ref _cache);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (IsFresh(cached, now))
        {
            return cached!.Configuration;
        }

        await _refreshLock.WaitAsync(cancel);
        try
        {
            // Kiểm tra lại sau khi giữ khóa: luồng khác có thể vừa tải xong
            cached = Volatile.Read(ref _cache);
            now = _timeProvider.GetUtcNow();
            if (IsFresh(cached, now))
            {
                return cached!.Configuration;
            }

            if (now < _retryAfterUtc && IsWithinMaxStale(cached, now))
            {
                return cached!.Configuration;
            }

            using HttpRequestMessage request = new(HttpMethod.Get, _jwksUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();

            JsonWebKeySet jwks = new(await response.Content.ReadAsStringAsync(cancel));
            OpenIdConnectConfiguration configuration = new() { Issuer = _options.Issuer };
            foreach (SecurityKey key in jwks.GetSigningKeys())
            {
                configuration.SigningKeys.Add(key);
            }

            Volatile.Write(ref _cache, new KeyCache(configuration, now, now.AddMinutes(Math.Max(1, _options.MaxStaleMinutes / 2))));
            Interlocked.Exchange(ref _refreshRequested, 0);
            return configuration;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (IsWithinMaxStale(cached, _timeProvider.GetUtcNow()))
        {
            // AuthService lỗi tạm thời: dùng key cũ còn trong hạn, thử lại sau 15 giây
            _retryAfterUtc = _timeProvider.GetUtcNow().AddSeconds(15);
            return cached!.Configuration;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    // Token ký bằng key mới (AuthService xoay key) → JwtBearer gọi hàm này; giới hạn 15 giây/lần
    public void RequestRefresh()
    {
        long nowTicks = _timeProvider.GetUtcNow().Ticks;
        long nextAllowed = Volatile.Read(ref _nextForcedRefreshTicks);
        if (nowTicks < nextAllowed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _nextForcedRefreshTicks, nowTicks + TimeSpan.FromSeconds(15).Ticks, nextAllowed) == nextAllowed)
        {
            Interlocked.Exchange(ref _refreshRequested, 1);
        }
    }

    public void Dispose() => _refreshLock.Dispose();

    private bool IsFresh(KeyCache? cache, DateTimeOffset now) =>
        IsWithinMaxStale(cache, now) && Volatile.Read(ref _refreshRequested) == 0 && now < cache!.RefreshAfterUtc;

    private bool IsWithinMaxStale(KeyCache? cache, DateTimeOffset now) =>
        cache is not null && now - cache.FetchedAtUtc <= TimeSpan.FromMinutes(_options.MaxStaleMinutes);
}
