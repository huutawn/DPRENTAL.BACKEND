using System.Net.Http.Headers;
using Dprental.PaymentService.Application.Security.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.PaymentService.Api.Security;

/// <summary>
/// Quản lý public keys tải từ JWKS endpoint của AuthService có cache và giới hạn timeout.
/// </summary>
public sealed class PaymentJwksConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly PaymentJwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Uri _jwksUri;

    private sealed record KeyCache(OpenIdConnectConfiguration Configuration, DateTimeOffset FetchedAtUtc, DateTimeOffset RefreshAfterUtc);
    private KeyCache? _cache;
    private int _refreshRequested;
    private long _nextForcedRefreshTicks;
    private DateTimeOffset _retryAfterUtc;

    public PaymentJwksConfigurationManager(
        HttpClient httpClient,
        PaymentJwtOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jwksUri = new Uri(_options.JwksUri, UriKind.Absolute);
    }

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();

        var cached = Volatile.Read(ref _cache);
        var now = _timeProvider.GetUtcNow();

        if (IsCacheWithinMaxStale(cached, now)
            && Volatile.Read(ref _refreshRequested) == 0
            && now < cached!.RefreshAfterUtc)
        {
            return cached.Configuration;
        }

        await _refreshLock.WaitAsync(cancel);
        try
        {
            cached = Volatile.Read(ref _cache);
            now = _timeProvider.GetUtcNow();

            if (IsCacheWithinMaxStale(cached, now)
                && Volatile.Read(ref _refreshRequested) == 0
                && now < cached!.RefreshAfterUtc)
            {
                return cached.Configuration;
            }

            if (now < _retryAfterUtc && IsCacheWithinMaxStale(cached, now))
            {
                return cached!.Configuration;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, _jwksUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancel);
            var jwks = new JsonWebKeySet(json);

            var config = new OpenIdConnectConfiguration
            {
                Issuer = _options.Issuer
            };

            foreach (var key in jwks.GetSigningKeys())
            {
                config.SigningKeys.Add(key);
            }

            var newCache = new KeyCache(
                config,
                now,
                now.AddMinutes(Math.Max(1, _options.MaxStaleMinutes / 2)));

            Volatile.Write(ref _cache, newCache);
            Interlocked.Exchange(ref _refreshRequested, 0);

            return config;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (IsCacheWithinMaxStale(cached, _timeProvider.GetUtcNow()))
        {
            _retryAfterUtc = _timeProvider.GetUtcNow().AddSeconds(15);
            return cached!.Configuration;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsCacheWithinMaxStale(KeyCache? cache, DateTimeOffset now) =>
        cache is not null && now - cache.FetchedAtUtc <= TimeSpan.FromMinutes(_options.MaxStaleMinutes);

    public void RequestRefresh()
    {
        var nowTicks = _timeProvider.GetUtcNow().Ticks;
        var nextAllowed = Volatile.Read(ref _nextForcedRefreshTicks);

        if (nowTicks < nextAllowed)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _nextForcedRefreshTicks,
                nowTicks + TimeSpan.FromSeconds(15).Ticks,
                nextAllowed) == nextAllowed)
        {
            Interlocked.Exchange(ref _refreshRequested, 1);
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }
}
