using System.IdentityModel.Tokens.Jwt;
using Dprental.AuthService.Api.Authentication.Configuration;
using Dprental.AuthService.Api.Authentication.Infrastructure;
using Dprental.AuthService.Application.Authentication.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AuthService.Api.Authentication;

internal static class JwtAuthenticationServiceCollectionExtensions
{
    internal static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetRequiredSection(JwtOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RsaJwtKeyRing>();
        services.AddSingleton<IAccessTokenIssuer, RsaAccessTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
        services.AddAuthorization();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RsaJwtKeyRing, IOptions<JwtOptions>>(ConfigureJwtBearer);

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions bearerOptions,
        RsaJwtKeyRing keyRing,
        IOptions<JwtOptions> options)
    {
        JwtOptions jwtOptions = options.Value;

        bearerOptions.MapInboundClaims = false;
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (_, _, keyId, _) => keyRing.ResolveVerificationKeys(keyId),
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
