using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;

namespace Dprental.AuthService.Api.Security;

/// <summary>Các extension đăng ký đăng nhập Google (OAuth 2.0 authorization code + PKCE) vào dependency injection container.</summary>
internal static class GoogleAuthenticationExtensions
{
    /// <summary>Tên scheme cookie tạm giữ danh tính Google trong lúc chuyển hướng giữa Google và callback của AuthService.</summary>
    internal const string ExternalScheme = "External";

    /// <summary>Đăng ký scheme Google; bỏ qua nếu chưa cấu hình ClientId/ClientSecret để môi trường chưa có Google vẫn chạy được.</summary>
    internal static IServiceCollection AddGoogleLogin(this IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(GoogleAuthOptions.SectionName);
        services.Configure<GoogleAuthOptions>(section);

        GoogleAuthOptions google = section.Get<GoogleAuthOptions>() ?? new GoogleAuthOptions();
        if (!google.IsConfigured)
        {
            return services;
        }

        // Không truyền default scheme: giữ JwtBearer làm scheme mặc định đã đặt ở AddJwtBearerSecurity.
        services.AddAuthentication()
            .AddCookie(ExternalScheme, options =>
            {
                options.Cookie.Name = "external_auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
            {
                options.SignInScheme = ExternalScheme;
                options.ClientId = google.ClientId;
                options.ClientSecret = google.ClientSecret;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.Scope.Add("email");
                options.Scope.Add("profile");
                options.ClaimActions.MapJsonKey(GoogleClaims.EmailVerified, GoogleClaims.EmailVerified);

                // Google quay về bằng điều hướng cấp cao nhất (GET), nên Lax là đủ. Mặc định SameSite=None yêu cầu Secure
                // và bị trình duyệt từ chối trên http://localhost khi phát triển.
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            });

        return services;
    }
}

/// <summary>Tên claim do Google trả về mà AuthService cần đọc.</summary>
internal static class GoogleClaims
{
    internal const string EmailVerified = "email_verified";
}
