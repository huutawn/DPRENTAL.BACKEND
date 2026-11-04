using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.NotificationService.Api.Security;

public static class SignalRAuthenticationExtensions
{
    public const string JwksHttpClientName = "notification-auth-jwks";

    public static IServiceCollection AddNotificationSecurityAndAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection(NotificationJwtOptions.SectionName);
        var issuer = jwtSection["Issuer"] ?? configuration["Jwt:Issuer"];
        var audience = jwtSection["Audience"] ?? configuration["Jwt:Audience"];
        var jwksUri = jwtSection["JwksUri"] ?? configuration["Jwt:JwksUri"];

        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new InvalidOperationException("Thiếu cấu hình bắt buộc Jwt:Issuer.");
        }
        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException("Thiếu cấu hình bắt buộc Jwt:Audience.");
        }
        if (string.IsNullOrWhiteSpace(jwksUri))
        {
            throw new InvalidOperationException("Thiếu cấu hình bắt buộc Jwt:JwksUri.");
        }

        var jwtOptions = new NotificationJwtOptions();
        jwtSection.Bind(jwtOptions);
        jwtOptions.Issuer = issuer;
        jwtOptions.Audience = audience;
        jwtOptions.JwksUri = jwksUri;

        services.AddSingleton(jwtOptions);

        services.AddHttpClient(JwksHttpClientName, (sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(jwtOptions.TimeoutSeconds > 0 ? jwtOptions.TimeoutSeconds : 10);
        });

        services.AddSingleton<NotificationJwksConfigurationManager>(sp =>
        {
            var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var client = clientFactory.CreateClient(JwksHttpClientName);
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new NotificationJwksConfigurationManager(client, jwtOptions, timeProvider);
        });

        services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
            sp.GetRequiredService<NotificationJwksConfigurationManager>());

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false;
            options.RefreshOnIssuerKeyNotFound = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = jwtOptions.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true,
                RequireExpirationTime = true
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // 1. Strictly reject query string token 'access_token'
                    if (context.Request.Query.ContainsKey("access_token"))
                    {
                        context.Fail("Query string access_token is strictly prohibited. Use Authorization header or accessToken cookie.");
                        return Task.CompletedTask;
                    }

                    // 2. Authorization header has precedence over cookie 'accessToken'. Cookie is fallback only.
                    var authHeader = context.Request.Headers.Authorization.ToString();
                    var hasAuthHeader = !string.IsNullOrWhiteSpace(authHeader);

                    if (!hasAuthHeader && string.IsNullOrEmpty(context.Token))
                    {
                        if (context.Request.Cookies.TryGetValue("accessToken", out var cookieToken) &&
                            !string.IsNullOrWhiteSpace(cookieToken))
                        {
                            context.Token = cookieToken;
                        }
                    }

                    return Task.CompletedTask;
                }
            };
        });

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<NotificationJwksConfigurationManager>((options, configManager) =>
            {
                options.ConfigurationManager = configManager;
            });

        services.AddAuthorization(options =>
        {
            var humanPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                {
                    var user = context.User;
                    if (user?.Identity == null || !user.Identity.IsAuthenticated)
                    {
                        return false;
                    }

                    // Reject service token: u: "svc"
                    var isServiceToken = string.Equals(user.FindFirst("u")?.Value, "svc", StringComparison.OrdinalIgnoreCase) ||
                                         user.Claims.Any(c => string.Equals(c.Type, "u", StringComparison.OrdinalIgnoreCase) &&
                                                               string.Equals(c.Value, "svc", StringComparison.OrdinalIgnoreCase));
                    if (isServiceToken)
                    {
                        return false;
                    }

                    // Must contain sub claim with valid User GUID
                    var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? user.FindFirst("sub")?.Value;

                    return !string.IsNullOrWhiteSpace(sub) && Guid.TryParse(sub, out var userId) && userId != Guid.Empty;
                })
                .Build();

            options.DefaultPolicy = humanPolicy;
            options.AddPolicy("HumanUserPolicy", humanPolicy);
        });

        return services;
    }
}
