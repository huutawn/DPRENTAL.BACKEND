using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.RentalService.Api.Security;

/// <summary>
/// Mở rộng IServiceCollection thiết lập JWT Bearer Authentication cho RentalService kết nối JWKS với AuthService.
/// </summary>
public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddRentalJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(RentalJwtOptions.SectionName);
        services.AddOptions<RentalJwtOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient("rental-auth-jwks", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RentalJwtOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<RentalJwksConfigurationManager>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RentalJwtOptions>>().Value;
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("rental-auth-jwks");
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new RentalJwksConfigurationManager(client, options, timeProvider);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<RentalJwksConfigurationManager, IOptions<RentalJwtOptions>>((bearerOptions, configManager, jwtOptionsAccessor) =>
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
                    ValidAudience = options.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    RequireSignedTokens = true,
                    RequireExpirationTime = true
                };
            });

        return services;
    }
}
