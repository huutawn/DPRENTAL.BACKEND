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
    private OpenIdConnectConfiguration? currentConfiguration;
    private DateTimeOffset fetchedAtUtc;
    private DateTimeOffset refreshAfterUtc;
    private int refreshRequested;

    /// <summary>Returns the cached key set or refreshes it when its bounded cache lifetime has elapsed.</summary>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        OpenIdConnectConfiguration? cachedConfiguration = Volatile.Read(ref currentConfiguration);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (cachedConfiguration is not null
            && Volatile.Read(ref refreshRequested) == 0
            && now < refreshAfterUtc)
        {
            return cachedConfiguration;
        }

        await refreshLock.WaitAsync(cancel);
        try
        {
            cachedConfiguration = currentConfiguration;
            now = timeProvider.GetUtcNow();
            if (cachedConfiguration is not null
                && Volatile.Read(ref refreshRequested) == 0
                && now < refreshAfterUtc)
            {
                return cachedConfiguration;
            }

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
                fetchedAtUtc = refreshedAtUtc;
                refreshAfterUtc = refreshedAtUtc.Add(GetRefreshInterval(response.Headers.CacheControl));
                Volatile.Write(ref currentConfiguration, configuration);
                Volatile.Write(ref refreshRequested, 0);
                return configuration;
            }
            catch when (cachedConfiguration is not null)
            {
                // Preserve known-good keys. Readiness reports the cache stale after MaximumUsableAge.
                return cachedConfiguration;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>Requests an immediate refresh, for example when JWT bearer encounters an unknown key ID.</summary>
    public void RequestRefresh() => Volatile.Write(ref refreshRequested, 1);

    /// <summary>Returns whether the key cache is valid enough for Gateway readiness.</summary>
    internal bool HasUsableConfiguration()
    {
        return Volatile.Read(ref currentConfiguration) is not null
            && timeProvider.GetUtcNow() - fetchedAtUtc <= MaximumUsableAge;
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
