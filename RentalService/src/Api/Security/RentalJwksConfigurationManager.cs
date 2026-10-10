using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.RentalService.Api.Security;

/// <summary>
/// Quản lý cấu hình public keys tải trực tiếp từ JWKS endpoint của AuthService.
/// </summary>
public sealed class RentalJwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RentalJwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Uri _jwksUri;

    private sealed record KeyCache(OpenIdConnectConfiguration Configuration, DateTimeOffset FetchedAtUtc, DateTimeOffset RefreshAfterUtc);
    private KeyCache? _cache;
    private int _refreshRequested;
    private long _nextForcedRefreshTicks;
    private DateTimeOffset _retryAfterUtc;

    public RentalJwksConfigurationManager(
        HttpClient httpClient,
        RentalJwtOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jwksUri = new Uri(_options.JwksUri, UriKind.Absolute);
    }

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

            if (now < _retryAfterUtc && cached is not null)
            {
                return cached.Configuration;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _jwksUri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancel);
                var jwks = new JsonWebKeySet(json);

                var config = new OpenIdConnectConfiguration();
                foreach (var key in jwks.GetSigningKeys())
                {
                    config.SigningKeys.Add(key);
                }

                var refreshAfter = now.AddMinutes(15);
                _cache = new KeyCache(config, now, refreshAfter);
                Volatile.Write(ref _refreshRequested, 0);

                return config;
            }
            catch when (cached is not null && now - cached.FetchedAtUtc <= TimeSpan.FromMinutes(_options.MaxStaleMinutes))
            {
                _retryAfterUtc = now.AddSeconds(30);
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

        if (Interlocked.CompareExchange(ref _nextForcedRefreshTicks, nowTicks + TimeSpan.FromSeconds(5).Ticks, nextTicks) == nextTicks)
        {
            Volatile.Write(ref _refreshRequested, 1);
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
