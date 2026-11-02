using Dprental.Gateway.Models.Authentication;
using Dprental.Gateway.Models.Errors;
using Dprental.Gateway.Extensions.Errors;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dprental.Gateway.Extensions.Authentication;

/// <summary>Đăng ký xác thực JWT bằng khóa công khai tải từ AuthService.</summary>
internal static class JwtAuthenticationServiceCollectionExtensions
{
    public const string InventoryScheme = "InventoryBearer";

    /// <summary>Đăng ký hai scheme JWT dùng chung JWKS; yêu cầu xác thực của từng route do Ocelot cấu hình.</summary>
    internal static IServiceCollection AddGatewayJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Kiểm tra cấu hình lúc khởi động; dùng chung cache JWKS cho cả hai scheme.
        services.AddSingleton<IValidateOptions<GatewayJwtOptions>, GatewayJwtOptionsValidator>();
        services.AddOptions<GatewayJwtOptions>()
            .Bind(configuration.GetRequiredSection(GatewayJwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("auth-jwks", client => client.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<JwksConfigurationManager>(serviceProvider =>
        {
            GatewayJwtOptions options = serviceProvider.GetRequiredService<IOptions<GatewayJwtOptions>>().Value;
            return new JwksConfigurationManager(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("auth-jwks"),
                options,
                serviceProvider.GetRequiredService<TimeProvider>());
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddJwtBearer(InventoryScheme);
        services.AddAuthorization();

        // Scheme Bearer mặc định chỉ chấp nhận audience người dùng được cấu hình (ví dụ dprental-clients).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwksConfigurationManager, IOptions<GatewayJwtOptions>>(ConfigureJwtBearer);

        // Scheme Inventory chọn audience người dùng hoặc inventory-api dựa trên claim loại danh tính.
        services.AddOptions<JwtBearerOptions>(InventoryScheme)
            .Configure<JwksConfigurationManager, IOptions<GatewayJwtOptions>>(ConfigureInventoryJwtBearer);

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions bearerOptions,
        JwksConfigurationManager configurationManager,
        IOptions<GatewayJwtOptions> options)
    {
        GatewayJwtOptions jwtOptions = options.Value;
        // Giữ nguyên tên claim từ token để sub và role được đọc nhất quán ở các bước tiếp theo.
        bearerOptions.MapInboundClaims = false;
        bearerOptions.ConfigurationManager = configurationManager;
        bearerOptions.RefreshOnIssuerKeyNotFound = true;
        bearerOptions.Events = CreateBearerEvents(configurationManager);
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            TryAllIssuerSigningKeys = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    }

    private static void ConfigureInventoryJwtBearer(
        JwtBearerOptions bearerOptions,
        JwksConfigurationManager configurationManager,
        IOptions<GatewayJwtOptions> options)
    {
        GatewayJwtOptions jwtOptions = options.Value;
        bearerOptions.MapInboundClaims = false;
        bearerOptions.ConfigurationManager = configurationManager;
        bearerOptions.RefreshOnIssuerKeyNotFound = true;
        bearerOptions.Events = CreateBearerEvents(configurationManager);
        bearerOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudiences = [jwtOptions.Audience, jwtOptions.InventoryAudience],
            // Token có u=svc phải mang audience Inventory; token còn lại phải mang audience người dùng.
            AudienceValidator = (audiences, securityToken, validationParameters) =>
            {
                if (audiences is null || !audiences.Any()) return false;
                var claims = securityToken switch
                {
                    Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt => jwt.Claims,
                    JwtSecurityToken jwt => jwt.Claims,
                    _ => []
                };
                bool service = claims.Any(c => c.Type == "u" && c.Value == "svc");
                return audiences.Contains(service ? jwtOptions.InventoryAudience : jwtOptions.Audience, StringComparer.Ordinal);
            },
            ValidateIssuerSigningKey = true,
            TryAllIssuerSigningKeys = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    }

    private static JwtBearerEvents CreateBearerEvents(JwksConfigurationManager configurationManager) => new()
    {
        OnMessageReceived = async context =>
        {
            // Chỉ lấy token từ cookie nếu Authorization trống và chưa có token được cung cấp trước đó.
            if (string.IsNullOrWhiteSpace(context.Request.Headers.Authorization) &&
                string.IsNullOrWhiteSpace(context.Token) &&
                context.Request.Cookies.TryGetValue("accessToken", out string? accessToken) &&
                !string.IsNullOrWhiteSpace(accessToken))
            {
                context.Token = accessToken;
            }

            string? token = context.Token;
            string authorization = context.Request.Headers.Authorization.ToString();
            if (token is null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authorization[7..].Trim();
            if (token is null || !new JwtSecurityTokenHandler().CanReadToken(token)) return;
            try
            {
                // Đọc kid để chuẩn bị khóa; việc đọc này chưa xác thực chữ ký hay độ tin cậy của token.
                // Bộ JWT Bearer vẫn kiểm tra chữ ký, issuer, audience và thời hạn sau sự kiện này.
                var jwt = new JwtSecurityToken(token);
                if (!await configurationManager.EnsureSigningKeyAsync(jwt.Header.Kid, context.HttpContext.RequestAborted))
                    context.Fail("Unknown signing key.");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
                ArgumentException or OperationCanceledException)
            {
                if (context.HttpContext.RequestAborted.IsCancellationRequested) throw;
                context.Fail("JWT signing keys are unavailable.");
            }
        }
    };
}
