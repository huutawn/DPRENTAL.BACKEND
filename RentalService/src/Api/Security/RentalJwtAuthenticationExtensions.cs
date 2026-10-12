using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.RentalService.Api.Security;

public static class RentalJwtAuthenticationExtensions
{
    private const string DevelopmentIssuer = "dprental-authservice-dev";
    private const string ClientAudience = "dprental-clients";

    public static IServiceCollection AddRentalJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string issuer = configuration["Auth:Jwt:Issuer"] ?? DevelopmentIssuer;
        string audience = configuration["Auth:Jwt:Audience"] ?? ClientAudience;
        Lazy<IReadOnlyCollection<SecurityKey>> signingKeys = new(
            () => LoadSigningKeys(configuration), LazyThreadSafetyMode.ExecutionAndPublication);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = "sub",
                    IssuerSigningKeyResolver = (_, _, tokenKeyId, _) => signingKeys.Value
                        .Where(key => string.IsNullOrWhiteSpace(tokenKeyId) ||
                            string.Equals(key.KeyId, tokenKeyId, StringComparison.Ordinal))
                        .ToArray()
                };
            });

        return services;
    }

    private static List<SecurityKey> LoadSigningKeys(IConfiguration configuration)
    {
        IConfigurationSection keySection = configuration.GetSection("Auth:Jwt:Keys");
        List<SecurityKey> keys = [];
        foreach (IConfigurationSection configuredKey in keySection.GetChildren())
        {
            string? keyId = configuredKey["KeyId"];
            string? publicKeyPem = configuredKey["PublicKeyPem"];
            if (string.IsNullOrWhiteSpace(publicKeyPem))
            {
                continue;
            }

            RSA rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(publicKeyPem);
                keys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
            }
            catch
            {
                rsa.Dispose();
                foreach (RsaSecurityKey key in keys.OfType<RsaSecurityKey>())
                {
                    key.Rsa?.Dispose();
                }
                throw;
            }
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                "Auth:Jwt:Keys must contain at least one AuthService public key PEM.");
        }

        return keys;
    }
}
