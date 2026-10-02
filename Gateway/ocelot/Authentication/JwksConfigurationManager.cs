using System.Net.Http.Headers;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.Gateway.Authentication;

/// <summary>
/// Loads AuthService's direct JWKS endpoint into the configuration shape consumed by JWT bearer authentication.
/// It keeps the last valid key set so a short AuthService outage does not invalidate already trusted keys.
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

    /// <summary>Returns the cached key set or refreshes it when its bounded cache lifetime has elapsed.</summary>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var cachedConfiguration = Volatile.Read(ref cache);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cachedConfiguration is not null
            && Volatile.Read(ref refreshRequested) == 0
            && now < cachedConfiguration.RefreshAfterUtc
            && now - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge)
        {
            return cachedConfiguration.Configuration;
        }

        await refreshLock.WaitAsync(cancel);
        try
        {
            cachedConfiguration = cache;
            now = timeProvider.GetUtcNow();
            if (cachedConfiguration is not null
                && Volatile.Read(ref refreshRequested) == 0
                && now < cachedConfiguration.RefreshAfterUtc
            && now - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge)
            {
                return cachedConfiguration.Configuration;
            }

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
                Volatile.Write(ref cache, new KeyCache(configuration, refreshedAtUtc,
                    refreshedAtUtc.Add(GetRefreshInterval(response.Headers.CacheControl))));
                Volatile.Write(ref refreshRequested, 0);
                return configuration;
            }
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

    /// <summary>Requests an immediate refresh, for example when JWT bearer encounters an unknown key ID.</summary>
    public void RequestRefresh()
    {
        long now = timeProvider.GetUtcNow().UtcTicks;
        long next = Interlocked.Read(ref nextForcedRefreshTicks);
        if (now >= next && Interlocked.CompareExchange(ref nextForcedRefreshTicks,
            now + TimeSpan.FromSeconds(10).Ticks, next) == next)
            Volatile.Write(ref refreshRequested, 1);
    }

    internal async Task<bool> EnsureSigningKeyAsync(string? kid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(kid)) return false;
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.SigningKeys.Any(k => k.KeyId == kid)) return true;
        RequestRefresh();
        configuration = await GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys.Any(k => k.KeyId == kid);
    }

    /// <summary>Returns whether the key cache is valid enough for Gateway readiness.</summary>
    internal bool HasUsableConfiguration()
    {
        var cachedConfiguration = Volatile.Read(ref cache);
        return cachedConfiguration is not null
            && timeProvider.GetUtcNow() - cachedConfiguration.FetchedAtUtc <= MaximumUsableAge;
    }

    /// <summary>Attempts an initial or stale-cache refresh for the readiness health check.</summary>
    internal async Task EnsureUsableConfigurationAsync(CancellationToken cancellationToken)
    {
        if (!HasUsableConfiguration())
        {
            RequestRefresh();
            await GetConfigurationAsync(cancellationToken);
        }
    }

    /// <summary>Releases the synchronization primitive owned by this singleton.</summary>
    public void Dispose() => refreshLock.Dispose();

    private static TimeSpan GetRefreshInterval(CacheControlHeaderValue? cacheControl)
    {
        TimeSpan requestedInterval = cacheControl?.MaxAge ?? DefaultRefreshInterval;
        return requestedInterval <= TimeSpan.Zero
            ? DefaultRefreshInterval
            : requestedInterval > MaximumRefreshInterval
                ? MaximumRefreshInterval
                : requestedInterval;
    }
}
