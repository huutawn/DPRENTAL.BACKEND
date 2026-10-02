using System.IdentityModel.Tokens.Jwt;
using Dprental.InventoryService.Common.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.InventoryService.Api.Security;

/// <summary>
/// Mở rộng IServiceCollection thiết lập JWT Bearer Authentication và kết nối JWKS với AuthService.
/// </summary>
public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddInventoryJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(JwtOptions.SectionName);
        services.AddOptions<JwtOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient("auth-jwks", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<JwtOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<JwksConfigurationManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<JwtOptions>>().Value;
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("auth-jwks");
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new JwksConfigurationManager(client, options, timeProvider);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksConfigurationManager, IOptions<JwtOptions>>((bearerOptions, configManager, jwtOptionsAccessor) =>
            {
                var options = jwtOptionsAccessor.Value;

                bearerOptions.MapInboundClaims = false;
                bearerOptions.ConfigurationManager = configManager;
                bearerOptions.RefreshOnIssuerKeyNotFound = true;

                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudiences = [options.HumanAudience, options.ServiceAudience],
                    AudienceValidator = (audiences, securityToken, validationParameters) =>
                    {
                        if (audiences is null || !audiences.Any()) return false;

                        string? u = null;
                        if (securityToken is JsonWebToken jsonWebToken)
                        {
                            u = jsonWebToken.Claims.FirstOrDefault(c => c.Type == "u")?.Value;
                        }
                        else if (securityToken is JwtSecurityToken legacyJwt)
                        {
                            u = legacyJwt.Claims.FirstOrDefault(c => c.Type == "u")?.Value;
                        }

                        if (u == "svc")
                        {
                            return audiences.Contains(options.ServiceAudience, StringComparer.Ordinal);
                        }

                        return audiences.Contains(options.HumanAudience, StringComparer.Ordinal);
                    },
                    IssuerSigningKeyResolver = (token, securityToken, kid, validationParameters) =>
                    {
                        var config = configManager.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();
                        var keys = config?.SigningKeys;
                        if (keys != null)
                        {
                            var matched = keys.Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal)).ToList();
                            if (matched.Count > 0)
                            {
                                return matched;
                            }
                        }

                        configManager.RequestRefresh();
                        return keys ?? [];
                    },
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "r"
                };

                bearerOptions.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception is SecurityTokenSignatureKeyNotFoundException
                            || context.Exception?.Message.Contains("key", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            configManager.RequestRefresh();
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var sub = context.Principal?.FindFirst("sub")?.Value;
                        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var subGuid) || subGuid == Guid.Empty)
                        {
                            context.Fail("Claim 'sub' phải là GUID hợp lệ và không được rỗng.");
                            return Task.CompletedTask;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }
}
