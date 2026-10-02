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

    private OpenIdConnectConfiguration? _currentConfiguration;
    private DateTimeOffset _fetchedAtUtc;
    private DateTimeOffset _refreshAfterUtc;
    private int _refreshRequested;

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
        var cached = Volatile.Read(ref _currentConfiguration);
        var now = _timeProvider.GetUtcNow();

        if (cached is not null
            && Volatile.Read(ref _refreshRequested) == 0
            && now < _refreshAfterUtc)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancel);
        try
        {
            cached = _currentConfiguration;
            now = _timeProvider.GetUtcNow();

            if (cached is not null
                && Volatile.Read(ref _refreshRequested) == 0
                && now < _refreshAfterUtc)
            {
                return cached;
            }

            try
            {
                using var response = await _httpClient.GetAsync(_jwksUri, cancel);
                response.EnsureSuccessStatusCode();

                string document = await response.Content.ReadAsStringAsync(cancel);
                var keySet = new JsonWebKeySet(document);
                if (keySet.Keys.Count == 0)
                {
                    throw new InvalidOperationException("AuthService JWKS document không chứa signing key nào.");
                }

                var configuration = new OpenIdConnectConfiguration();
                foreach (var key in keySet.Keys)
                {
                    configuration.SigningKeys.Add(key);
                }

                DateTimeOffset refreshedAt = _timeProvider.GetUtcNow();
                _fetchedAtUtc = refreshedAt;
                _refreshAfterUtc = refreshedAt.Add(GetRefreshInterval(response.Headers.CacheControl));

                Volatile.Write(ref _currentConfiguration, configuration);
                Volatile.Write(ref _refreshRequested, 0);

                return configuration;
            }
            catch when (cached is not null && (_timeProvider.GetUtcNow() - _fetchedAtUtc) <= TimeSpan.FromMinutes(_options.MaxStaleMinutes))
            {
                // Cho phép tái sử dụng key cache trong giới hạn MaxStaleMinutes khi gặp sự cố mạng tạm thời
                return cached;
            }
            catch (Exception ex)
            {
                if (cached is not null && (_timeProvider.GetUtcNow() - _fetchedAtUtc) > TimeSpan.FromMinutes(_options.MaxStaleMinutes))
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
        Volatile.Write(ref _refreshRequested, 1);
    }

    /// <summary>
    /// Kiểm tra cache hiện có còn khả dụng hay không (phục vụ health check).
    /// </summary>
    public bool HasUsableConfiguration()
    {
        return _currentConfiguration is not null
            && (_timeProvider.GetUtcNow() - _fetchedAtUtc) <= TimeSpan.FromMinutes(_options.MaxStaleMinutes);
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
