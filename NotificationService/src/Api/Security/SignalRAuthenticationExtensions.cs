using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.NotificationService.Api.Security;

public static class SignalRAuthenticationExtensions
{
    public static IServiceCollection AddNotificationSecurityAndAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSection = configuration.GetSection("Jwt");
        var authority = jwtSection["Authority"];
        var issuer = jwtSection["Issuer"] ?? "https://auth.dprental.vn";
        var audience = jwtSection["Audience"] ?? "dprental-platform";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            if (!string.IsNullOrWhiteSpace(authority))
            {
                options.Authority = authority;
            }

            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
                ValidIssuer = issuer,
                ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // 1. Kiểm tra query string token: TUYỆT ĐỐI TỪ CHỐI
                    if (context.Request.Query.ContainsKey("access_token"))
                    {
                        context.Fail("Query string access_token is strictly prohibited. Use Authorization header or accessToken cookie.");
                        return Task.CompletedTask;
                    }

                    // 2. Kiểm tra cookie 'accessToken' nếu thiếu header
                    if (string.IsNullOrEmpty(context.Token) &&
                        context.Request.Cookies.TryGetValue("accessToken", out var cookieToken) &&
                        !string.IsNullOrWhiteSpace(cookieToken))
                    {
                        context.Token = cookieToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorization();

        return services;
    }
}
