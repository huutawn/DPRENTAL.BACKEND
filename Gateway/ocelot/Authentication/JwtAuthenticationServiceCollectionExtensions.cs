using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.Gateway.Authentication;

/// <summary>Registers Gateway JWT authentication using public keys retrieved from AuthService.</summary>
internal static class JwtAuthenticationServiceCollectionExtensions
{
    /// <summary>Registers direct-JWKS JWT bearer authentication and a deny-by-default authorization policy.</summary>
    internal static IServiceCollection AddGatewayJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<GatewayJwtOptions>, GatewayJwtOptionsValidator>();
        services.AddOptions<GatewayJwtOptions>()
            .Bind(configuration.GetRequiredSection(GatewayJwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("auth-jwks", client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<JwksConfigurationManager>(serviceProvider =>
        {
            GatewayJwtOptions options = serviceProvider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value;
            return new JwksConfigurationManager(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("auth-jwks"),
                options,
                serviceProvider.GetRequiredService<TimeProvider>());
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddAuthorization();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksConfigurationManager, IOptions<GatewayJwtOptions>>(ConfigureJwtBearer);

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions bearerOptions,
        JwksConfigurationManager configurationManager,
        IOptions<GatewayJwtOptions> options)
    {
        GatewayJwtOptions jwtOptions = options.Value;
        bearerOptions.MapInboundClaims = false;
        bearerOptions.ConfigurationManager = configurationManager;
        bearerOptions.RefreshOnIssuerKeyNotFound = true;
        bearerOptions.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var routePolicy = context.HttpContext.RequestServices.GetService<ApiKeyRoutePolicy>();
                if (routePolicy?.RequiresApiKey(context.Request) == true)
                {
                    var identity = new ClaimsIdentity([new Claim("u", "svc")], "Bearer");
                    context.Principal = new ClaimsPrincipal(identity);
                    context.Success();
                    return Task.CompletedTask;
                }

                if (string.IsNullOrWhiteSpace(context.Request.Headers.Authorization) &&
                    string.IsNullOrWhiteSpace(context.Token) &&
                    context.Request.Cookies.TryGetValue("accessToken", out string? accessToken) &&
                    !string.IsNullOrWhiteSpace(accessToken))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    }
}
