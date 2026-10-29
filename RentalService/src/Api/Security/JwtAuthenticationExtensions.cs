using Dprental.RentalService.Api.Hubs.Security;
using Dprental.RentalService.Application.Security;
using Microsoft.Net.Http.Headers;
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
/// <remarks>
/// Program gọi khi startup để đăng ký xác thực JWT và bộ quản lý khóa JWKS.
/// Middleware xác thực trước khi Application dựng RentalCallerContext; token hợp lệ chưa đủ chứng minh quyền trên order/item.
/// </remarks>
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

                bearerOptions.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Chỉ cho phép đọc cookie accessToken tại OrderHub và endpoint negotiate.
                        if (OrderHubPathHelper.IsOrderHubPath(context.Request.Path))
                        {
                            // Authorization header có ưu tiên: nếu header đã có, không được fallback sang cookie.
                            if (!context.Request.Headers.ContainsKey(HeaderNames.Authorization) &&
                                string.IsNullOrWhiteSpace(context.Token))
                            {
                                if (context.Request.Cookies.TryGetValue(OrderHubSecurityConstants.AccessTokenCookieName, out string? cookieToken) &&
                                    !string.IsNullOrWhiteSpace(cookieToken))
                                {
                                    context.Token = cookieToken;
                                }
                            }
                        }

                        // Tuyệt đối không đọc access_token từ query string cho Hub hoặc REST.
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }
}
