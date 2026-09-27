using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.MediaService.Api.Security;

// AuthService exposes a JWKS document rather than an OIDC discovery document.
public sealed class JwksConfigurationManager(HttpClient httpClient, IOptions<JwtOptions> options)
    : IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenIdConnectConfiguration? _current;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _refreshAfter;
    private volatile bool _forceRefresh;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        if (_current is not null && !_forceRefresh && DateTimeOffset.UtcNow < _refreshAfter)
        {
            return _current;
        }

        await _gate.WaitAsync(cancel);
        try
        {
            if (_current is not null && !_forceRefresh && DateTimeOffset.UtcNow < _refreshAfter)
            {
                return _current;
            }

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync(options.Value.JwksUri, cancel);
                response.EnsureSuccessStatusCode();
                var keys = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancel));
                if (keys.Keys.Count == 0)
                {
                    throw new InvalidOperationException("AuthService JWKS contains no signing keys.");
                }

                var configuration = new OpenIdConnectConfiguration();
                foreach (JsonWebKey key in keys.Keys)
                {
                    configuration.SigningKeys.Add(key);
                }

                _current = configuration;
                _fetchedAt = DateTimeOffset.UtcNow;
                _refreshAfter = _fetchedAt.AddMinutes(5);
                _forceRefresh = false;
                return configuration;
            }
            catch when (!cancel.IsCancellationRequested && _current is not null && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromMinutes(10))
            {
                _forceRefresh = false;
                _refreshAfter = DateTimeOffset.UtcNow.AddSeconds(30);
                return _current;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestRefresh() => _forceRefresh = true;

    public void Dispose() => _gate.Dispose();
}
