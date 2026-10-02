using System.Net.Http.Headers;
using Dprental.CatalogService.Application.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.CatalogService.Api.Security;

/// <summary>
/// Tải public key trực tiếp từ JWKS endpoint của AuthService (AuthService không có OIDC discovery document).
/// Cache có thời hạn, tải lại khi gặp <c>kid</c> lạ và không dùng cache cũ quá <see cref="JwtOptions.MaxStaleMinutes"/>.
/// </summary>
internal sealed class JwksConfigurationManager(
    HttpClient httpClient,
    JwtOptions options,
    TimeProvider timeProvider) : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    /// <summary>Độ dài RSA tối thiểu được chấp nhận để bảo vệ chữ ký JWT.</summary>
    private const int MinimumRsaKeySizeInBits = 2048;

    /// <summary>Khoảng cách tối thiểu giữa hai lần tải lại bị ép, để token giả <c>kid</c> không dội request sang AuthService.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly Uri jwksUri = new(options.JwksUri, UriKind.Absolute);
    private readonly TimeSpan cacheDuration = TimeSpan.FromMinutes(options.CacheDurationMinutes);
    private readonly TimeSpan maxStale = TimeSpan.FromMinutes(options.MaxStaleMinutes);
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    private KeyCache? cache;
    private int refreshRequested;
    private long nextForcedRefreshTicks;
    private DateTimeOffset retryAfterUtc;

    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        KeyCache? cached = Volatile.Read(ref cache);
        if (IsFresh(cached))
        {
            return cached!.Configuration;
        }

        await refreshLock.WaitAsync(cancel);
        try
        {
            cached = cache;
            if (IsFresh(cached))
            {
                return cached!.Configuration;
            }

            bool cachedIsTrusted = cached is not null && timeProvider.GetUtcNow() - cached.FetchedAtUtc <= maxStale;
            if (timeProvider.GetUtcNow() < retryAfterUtc)
            {
                return cachedIsTrusted
                    ? cached!.Configuration
                    : throw new InvalidOperationException("JWKS của AuthService tạm thời không khả dụng.");
            }

            try
            {
                KeyCache refreshed = await FetchAsync(cancel);
                Volatile.Write(ref cache, refreshed);
                Volatile.Write(ref refreshRequested, 0);
                return refreshed.Configuration;
            }
            catch (Exception) when (!cancel.IsCancellationRequested)
            {
                // Giãn nhịp thử lại khi AuthService lỗi, nhưng không kéo dài thời hạn tin cache ban đầu.
                retryAfterUtc = timeProvider.GetUtcNow().Add(RetryInterval);
                Volatile.Write(ref refreshRequested, 0);
                if (cachedIsTrusted)
                {
                    return cached!.Configuration;
                }

                throw;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        long now = timeProvider.GetUtcNow().UtcTicks;
        long next = Interlocked.Read(ref nextForcedRefreshTicks);
        if (now >= next
            && Interlocked.CompareExchange(ref nextForcedRefreshTicks, now + RetryInterval.Ticks, next) == next)
        {
            Volatile.Write(ref refreshRequested, 1);
        }
    }

    /// <summary>Tải lại JWKS nếu gặp <c>kid</c> chưa biết, trước khi request hiện tại được xác thực chữ ký.</summary>
    public async Task<bool> EnsureSigningKeyAsync(string? keyId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            return false;
        }

        OpenIdConnectConfiguration configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.SigningKeys.Any(key => key.KeyId == keyId))
        {
            return true;
        }

        RequestRefresh();
        configuration = await GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys.Any(key => key.KeyId == keyId);
    }

    public void Dispose() => refreshLock.Dispose();

    private bool IsFresh(KeyCache? cached)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        return cached is not null
            && Volatile.Read(ref refreshRequested) == 0
            && now < cached.RefreshAfterUtc
            && now - cached.FetchedAtUtc <= maxStale;
    }

    private async Task<KeyCache> FetchAsync(CancellationToken cancel)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(jwksUri, cancel);
        response.EnsureSuccessStatusCode();

        JsonWebKeySet keySet = new(await response.Content.ReadAsStringAsync(cancel));
        OpenIdConnectConfiguration configuration = new();
        foreach (JsonWebKey key in keySet.Keys.Where(IsAcceptedSigningKey))
        {
            configuration.SigningKeys.Add(key);
        }

        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException("JWKS của AuthService không chứa signing key RS256 hợp lệ nào.");
        }

        DateTimeOffset fetchedAt = timeProvider.GetUtcNow();
        return new KeyCache(configuration, fetchedAt, fetchedAt.Add(GetRefreshInterval(response.Headers.CacheControl)));
    }

    private static bool IsAcceptedSigningKey(JsonWebKey key) =>
        key.Kty == JsonWebAlgorithmsKeyTypes.RSA
        && !string.IsNullOrWhiteSpace(key.Kid)
        && !key.HasPrivateKey
        && (string.IsNullOrEmpty(key.Use) || key.Use == JsonWebKeyUseNames.Sig)
        && (string.IsNullOrEmpty(key.Alg) || key.Alg == SecurityAlgorithms.RsaSha256)
        && key.KeySize >= MinimumRsaKeySizeInBits;

    /// <summary>Tôn trọng <c>Cache-Control: max-age</c> của AuthService nếu ngắn hơn cấu hình, để key rotation có hiệu lực sớm.</summary>
    private TimeSpan GetRefreshInterval(CacheControlHeaderValue? cacheControl) =>
        cacheControl?.MaxAge is { } maxAge && maxAge > TimeSpan.Zero && maxAge < cacheDuration
            ? maxAge
            : cacheDuration;

    private sealed record KeyCache(
        OpenIdConnectConfiguration Configuration,
        DateTimeOffset FetchedAtUtc,
        DateTimeOffset RefreshAfterUtc);
}
