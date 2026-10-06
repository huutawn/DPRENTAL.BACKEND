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
                bearerOptions.SaveToken = true;
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
                    TryAllIssuerSigningKeys = false,
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
                    OnMessageReceived = async context =>
                    {
                        string authorization = context.Request.Headers.Authorization.ToString();
                        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return;
                        string token = authorization[7..].Trim();
                        if (!new JsonWebTokenHandler().CanReadToken(token)) return;
                        try
                        {
                            // An unvalidated kid only selects a public key; claims remain untrusted.
                            var jwt = new JsonWebToken(token);
                            if (!await configManager.EnsureSigningKeyAsync(jwt.Kid, context.HttpContext.RequestAborted))
                                context.Fail("Unknown signing key.");
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                            System.Text.Json.JsonException or ArgumentException or OperationCanceledException)
                        {
                            if (context.HttpContext.RequestAborted.IsCancellationRequested) throw;
                            context.Fail("JWT signing keys are unavailable.");
                        }
                    },
                    OnTokenValidated = context =>
                    {
                        var tokenUses = context.Principal?.FindAll("u").Select(c => c.Value).ToArray() ?? [];
                        if (tokenUses.Length > 1 || tokenUses.Length == 1 && tokenUses[0] != "svc")
                        {
                            context.Fail("Unsupported token use.");
                            return Task.CompletedTask;
                        }
                        var subjects = context.Principal?.FindAll("sub").ToArray() ?? [];
                        if (subjects.Length != 1)
                        {
                            context.Fail("A single subject is required.");
                            return Task.CompletedTask;
                        }
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
