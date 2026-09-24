using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Dprental.AuthService.Api.Security;

/// <summary>Cấu hình và tiện ích quản lý Cookie phiên đăng nhập (accessToken, refreshToken).</summary>
internal static class AuthCookies
{
    internal const string AccessTokenCookieName = "accessToken";
    internal const string RefreshTokenCookieName = "refreshToken";

    internal static CookieOptions CreateOptions(HttpRequest request, IWebHostEnvironment environment, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);

        bool isHttps = request.IsHttps;
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = environment.IsProduction() || isHttps,
            SameSite = isHttps ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(lifetime)
        };
    }

    internal static void SetAuthCookies(
        HttpResponse response,
        HttpRequest request,
        IWebHostEnvironment environment,
        string accessToken,
        string refreshToken,
        int accessTokenLifetimeMinutes)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(
            AccessTokenCookieName,
            accessToken,
            CreateOptions(request, environment, TimeSpan.FromMinutes(accessTokenLifetimeMinutes)));

        response.Cookies.Append(
            RefreshTokenCookieName,
            refreshToken,
            CreateOptions(request, environment, TimeSpan.FromDays(30)));
    }

    internal static void Clear(HttpResponse response, HttpRequest request, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(AccessTokenCookieName, CreateOptions(request, environment, TimeSpan.Zero));
        response.Cookies.Delete(RefreshTokenCookieName, CreateOptions(request, environment, TimeSpan.Zero));
    }
}
