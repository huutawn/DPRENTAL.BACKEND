using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AnalyticsService.Api.Security;

public static class AnalyticsJwtAuthentication
{
    public const string AdminPolicy = "AnalyticsAdmin";

    public static IServiceCollection AddAnalyticsSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        string issuer = configuration["Authentication:Jwt:Issuer"] ?? "dprental-authservice-dev";
        string audience = configuration["Authentication:Jwt:Audience"] ?? "dprental-clients";
        string jwksUri = configuration["Authentication:Jwt:JwksUri"]
            ?? "http://auth-service:8080/.well-known/jwks.json";

        services.AddHttpClient<AnalyticsJwksManager>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AnalyticsJwksManager>((options, jwksManager) =>
            {
                options.MapInboundClaims = false;
                options.RefreshOnIssuerKeyNotFound = true;
                options.ConfigurationManager = jwksManager;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true
                };
            });

        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                !context.User.HasClaim("u", "svc") &&
                context.User.FindAll("r").Any(claim =>
                    claim.Value is "adm" or "adp" or "admin" or "admin_platform")));
        return services;
    }
}

public sealed class AnalyticsJwksManager(HttpClient client, IConfiguration configuration) :
    IConfigurationManager<OpenIdConnectConfiguration>, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenIdConnectConfiguration? _cached;
    private DateTimeOffset _refreshAfter;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _refreshAfter) return _cached;
        await _gate.WaitAsync(cancel);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _refreshAfter) return _cached;
            string uri = configuration["Authentication:Jwt:JwksUri"]
                ?? "http://auth-service:8080/.well-known/jwks.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            var keys = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancel));
            var result = new OpenIdConnectConfiguration();
            foreach (var key in keys.GetSigningKeys()) result.SigningKeys.Add(key);
            _cached = result;
            _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(15);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestRefresh() => _refreshAfter = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();
}
