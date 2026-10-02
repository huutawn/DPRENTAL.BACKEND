using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.InventoryService.Api.Security;

/// <summary>
/// Quản lý cấu hình public keys tải trực tiếp từ JWKS endpoint của AuthService.
/// Hỗ trợ refresh khi gặp unknown kid, bounded cache lifetime, và giới hạn tối đa stale không quá MaxStaleMinutes.
/// </summary>
public sealed class JwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Uri _jwksUri;

    private sealed record KeyCache(OpenIdConnectConfiguration Configuration, DateTimeOffset FetchedAtUtc, DateTimeOffset RefreshAfterUtc);
    private KeyCache? _cache;
    private int _refreshRequested;
    private long _nextForcedRefreshTicks;
    private DateTimeOffset _retryAfterUtc;

    public JwksConfigurationManager(
        HttpClient httpClient,
        JwtOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jwksUri = new Uri(_options.JwksUri, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var cached = Volatile.Read(ref _cache);
        var now = _timeProvider.GetUtcNow();

        if (cached is not null
            && Volatile.Read(ref _refreshRequested) == 0
            && now < cached.RefreshAfterUtc
            && now - cached.FetchedAtUtc <= TimeSpan.FromMinutes(_options.MaxStaleMinutes))
        {
            return cached.Configuration;
        }

        await _refreshLock.WaitAsync(cancel);
        try
        {
            cached = _cache;
            now = _timeProvider.GetUtcNow();

            if (cached is not null
                && Volatile.Read(ref _refreshRequested) == 0
                && now < cached.RefreshAfterUtc
            && now - cached.FetchedAtUtc <= TimeSpan.FromMinutes(_options.MaxStaleMinutes))
            {
                return cached.Configuration;
            }

            if (now < _retryAfterUtc)
                throw new InvalidOperationException("JWKS refresh is temporarily unavailable.");

            try
            {
                using var response = await _httpClient.GetAsync(_jwksUri, cancel);
                response.EnsureSuccessStatusCode();

                string document = await response.Content.ReadAsStringAsync(cancel);
                var keySet = new JsonWebKeySet(document);
                var signingKeys = keySet.Keys.Where(k => k.Kty == "RSA" && !string.IsNullOrWhiteSpace(k.Kid) &&
                    (string.IsNullOrEmpty(k.Use) || k.Use == "sig") &&
                    (string.IsNullOrEmpty(k.Alg) || k.Alg == "RS256")).ToArray();
                if (signingKeys.Length == 0)
                {
                    throw new InvalidOperationException("AuthService JWKS document không chứa signing key nào.");
                }

                var configuration = new OpenIdConnectConfiguration();
                foreach (var key in signingKeys)
                {
                    configuration.SigningKeys.Add(key);
                }

                DateTimeOffset refreshedAt = _timeProvider.GetUtcNow();
                Volatile.Write(ref _cache, new KeyCache(configuration, refreshedAt,
                    refreshedAt.Add(GetRefreshInterval(response.Headers.CacheControl))));
                Volatile.Write(ref _refreshRequested, 0);

                return configuration;
            }
            catch when (!cancel.IsCancellationRequested && cached is not null && (_timeProvider.GetUtcNow() - cached.FetchedAtUtc) <= TimeSpan.FromMinutes(_options.MaxStaleMinutes))
            {
                // Bound outage retries without extending the original key trust deadline.
                Volatile.Write(ref _cache, cached with { RefreshAfterUtc = _timeProvider.GetUtcNow().AddSeconds(10) });
                Volatile.Write(ref _refreshRequested, 0);
                return cached.Configuration;
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested) throw;
                _retryAfterUtc = _timeProvider.GetUtcNow().AddSeconds(10);
                if (cached is not null && (_timeProvider.GetUtcNow() - cached.FetchedAtUtc) > TimeSpan.FromMinutes(_options.MaxStaleMinutes))
                {
                    throw new InvalidOperationException(
                        $"JWKS key cache đã vượt quá giới hạn stale tối đa ({_options.MaxStaleMinutes} phút) và không thể kết nối tới AuthService.", ex);
                }

                throw;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        long now = _timeProvider.GetUtcNow().UtcTicks;
        long next = Interlocked.Read(ref _nextForcedRefreshTicks);
        if (now >= next && Interlocked.CompareExchange(ref _nextForcedRefreshTicks,
            now + TimeSpan.FromSeconds(10).Ticks, next) == next)
        {
            Volatile.Write(ref _refreshRequested, 1);
        }
    }

    /// <summary>Refreshes an unknown kid before the current request is validated.</summary>
    public async Task<bool> EnsureSigningKeyAsync(string? kid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kid)) return false;
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.SigningKeys.Any(k => k.KeyId == kid)) return true;
        RequestRefresh();
        configuration = await GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys.Any(k => k.KeyId == kid);
    }

    /// <summary>
    /// Kiểm tra cache hiện có còn khả dụng hay không (phục vụ health check).
    /// </summary>
    public bool HasUsableConfiguration()
    {
        var cached = Volatile.Read(ref _cache);
        return cached is not null
            && (_timeProvider.GetUtcNow() - cached.FetchedAtUtc) <= TimeSpan.FromMinutes(_options.MaxStaleMinutes);
    }

    private TimeSpan GetRefreshInterval(CacheControlHeaderValue? cacheControl)
    {
        TimeSpan configured = TimeSpan.FromMinutes(_options.CacheDurationMinutes);
        if (cacheControl?.MaxAge.HasValue == true && cacheControl.MaxAge.Value > TimeSpan.Zero)
        {
            return cacheControl.MaxAge.Value < configured ? cacheControl.MaxAge.Value : configured;
        }

        return configured;
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
