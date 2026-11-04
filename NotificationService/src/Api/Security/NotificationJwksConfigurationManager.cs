using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.NotificationService.Api.Security;

/// <summary>
/// Quản lý cấu hình public keys tải trực tiếp từ JWKS endpoint của AuthService với cơ chế caching và key rotation.
/// </summary>
public sealed class NotificationJwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly NotificationJwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Uri _jwksUri;

    private sealed record KeyCache(
        OpenIdConnectConfiguration Configuration,
        DateTimeOffset FetchedAtUtc,
        DateTimeOffset RefreshAfterUtc);

    private KeyCache? _cache;
    private int _refreshRequested;
    private long _nextForcedRefreshTicks;
    private DateTimeOffset _retryAfterUtc;

    public NotificationJwksConfigurationManager(
        HttpClient httpClient,
        NotificationJwtOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrWhiteSpace(_options.JwksUri) || !Uri.TryCreate(_options.JwksUri, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Jwt:JwksUri must be a valid absolute URI.", nameof(options));
        }
        _jwksUri = uri;
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var cached = Volatile.Read(ref _cache);
        var now = _timeProvider.GetUtcNow();
        var maxStale = TimeSpan.FromMinutes(_options.MaxStaleMinutes > 0 ? _options.MaxStaleMinutes : 60);

        if (cached is not null
            && Volatile.Read(ref _refreshRequested) == 0
            && now < cached.RefreshAfterUtc
            && now - cached.FetchedAtUtc <= maxStale)
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
                && now - cached.FetchedAtUtc <= maxStale)
            {
                return cached.Configuration;
            }

            if (now < _retryAfterUtc && cached is not null && now - cached.FetchedAtUtc <= maxStale)
            {
                return cached.Configuration;
            }

            try
            {
                var timeoutSeconds = _options.TimeoutSeconds > 0 ? _options.TimeoutSeconds : 10;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, _jwksUri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var jwks = new JsonWebKeySet(json);

                var config = new OpenIdConnectConfiguration
                {
                    Issuer = _options.Issuer
                };
                foreach (var key in jwks.GetSigningKeys())
                {
                    config.SigningKeys.Add(key);
                }

                var refreshMinutes = _options.RefreshIntervalMinutes > 0 ? _options.RefreshIntervalMinutes : 15;
                var refreshAfter = now.AddMinutes(refreshMinutes);

                _cache = new KeyCache(config, now, refreshAfter);
                Volatile.Write(ref _refreshRequested, 0);

                return config;
            }
            catch (Exception) when (cached is not null && now - cached.FetchedAtUtc <= maxStale)
            {
                var throttleSeconds = _options.RefreshThrottleSeconds > 0 ? _options.RefreshThrottleSeconds : 30;
                _retryAfterUtc = now.AddSeconds(throttleSeconds);
                return cached.Configuration;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void RequestRefresh()
    {
        var nowTicks = _timeProvider.GetUtcNow().Ticks;
        var nextTicks = Volatile.Read(ref _nextForcedRefreshTicks);

        if (nowTicks < nextTicks)
        {
            return;
        }

        var throttleSeconds = _options.RefreshThrottleSeconds > 0 ? _options.RefreshThrottleSeconds : 30;
        if (Interlocked.CompareExchange(ref _nextForcedRefreshTicks, nowTicks + TimeSpan.FromSeconds(throttleSeconds).Ticks, nextTicks) == nextTicks)
        {
            Volatile.Write(ref _refreshRequested, 1);
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
