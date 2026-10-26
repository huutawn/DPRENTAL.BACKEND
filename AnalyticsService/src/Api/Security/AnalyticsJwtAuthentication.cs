using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Application.Hub.Security;
using Dprental.AnalyticsService.Application.Security;
using Dprental.AnalyticsService.Application.Security.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.AnalyticsService.Api.Security;

/// <summary>
/// Xác thực JWT của AuthService cho dashboard API và SignalR Hub. Giữ tên và cách dùng của bản trước
/// (AddAnalyticsSecurity, AdminPolicy); bổ sung: cấu hình được kiểm tra lúc khởi động, chỉ nhận RS256,
/// dùng tiếp key cũ khi AuthService lỗi tạm thời, lấy token từ cookie cho Hub, quy tắc admin dùng chung.
/// </summary>
public static class AnalyticsJwtAuthentication
{
    public const string AdminPolicy = "AnalyticsAdmin";
    private const string JwksHttpClientName = "analytics-auth-jwks";

    public static IServiceCollection AddAnalyticsSecurity(this IServiceCollection services, IConfiguration configuration)
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
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

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
                        // với Hub, lấy token từ cookie accessToken giống PaymentHub
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
                    bearer.RefreshOnIssuerKeyNotFound = true;
                    bearer.ConfigurationManager = keys;
                    bearer.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwt.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwt.Audience,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                        RequireSignedTokens = true,
                        RequireExpirationTime = true,
                        ValidAlgorithms = [SecurityAlgorithms.RsaSha256] // Chặn tấn công đổi thuật toán
                    };
                });

        // Admin = người dùng thật có role admin; token service (u=svc) luôn bị từ chối
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.IsPlatformAdmin()));

        return services;
    }
}
