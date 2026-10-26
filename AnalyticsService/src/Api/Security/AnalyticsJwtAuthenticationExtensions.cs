using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Application.Hub.Security;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AnalyticsService.Api.Security;

/// <summary>
/// Xác thực JWT Bearer cho AnalyticsService, cùng cách PaymentService: kiểm tra chữ ký RS256 bằng
/// public key từ JWKS của AuthService, kiểm tra issuer, audience, hạn dùng.
/// </summary>
public static class AnalyticsJwtAuthenticationExtensions
{
    private const string JwksHttpClientName = "analytics-auth-jwks";

    public static IServiceCollection AddAnalyticsJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AnalyticsJwtOptions>()
            .Bind(configuration.GetSection(AnalyticsJwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(JwksHttpClientName, (provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<AnalyticsJwtOptions>>().Value.TimeoutSeconds));

        services.AddSingleton(provider => new AnalyticsJwksConfigurationManager(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(JwksHttpClientName),
            provider.GetRequiredService<IOptions<AnalyticsJwtOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AnalyticsJwksConfigurationManager, IOptions<AnalyticsJwtOptions>, IOptions<AnalyticsHubOptions>>(
                (bearer, keys, jwtAccessor, hubAccessor) =>
                {
                    AnalyticsJwtOptions jwt = jwtAccessor.Value;
                    string hubPath = hubAccessor.Value.HubPath;

                    bearer.Events = new JwtBearerEvents
                    {
                        // Trình duyệt không gắn được header Authorization cho WebSocket:
                        // với Hub, lấy token từ cookie accessToken giống PaymentService
                        OnMessageReceived = context =>
                        {
                            if (string.IsNullOrWhiteSpace(context.Token)
                                && context.Request.Path.StartsWithSegments(hubPath, StringComparison.OrdinalIgnoreCase)
                                && context.Request.Cookies.TryGetValue(AnalyticsHubSecurityConstants.AccessTokenCookieName, out string? cookieToken)
                                && !string.IsNullOrWhiteSpace(cookieToken))
                            {
                                context.Token = cookieToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                    bearer.MapInboundClaims = false; // Giữ nguyên tên claim gốc: sub, aud, r, u
                    bearer.ConfigurationManager = keys;
                    bearer.RefreshOnIssuerKeyNotFound = true;
                    bearer.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwt.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwt.ClientAudience,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
                    };
                });

        return services;
    }
}
