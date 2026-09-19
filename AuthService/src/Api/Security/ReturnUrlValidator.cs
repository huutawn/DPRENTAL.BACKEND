using System.Diagnostics.CodeAnalysis;

namespace Dprental.AuthService.Api.Security;

/// <summary>Kiểm tra returnUrl để đăng nhập Google không bị lợi dụng làm open redirect sang trang giả mạo.</summary>
internal static class ReturnUrlValidator
{
    /// <summary>Chỉ chấp nhận đường dẫn tương đối cùng origin hoặc URL tuyệt đối có origin nằm trong danh sách cho phép.</summary>
    internal static bool IsAllowed([NotNullWhen(true)] string? returnUrl, IReadOnlyCollection<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return false;
        }

        if (returnUrl.StartsWith('/'))
        {
            // "//host" và "/\host" đều bị trình duyệt hiểu là host khác, không phải đường dẫn nội bộ.
            return returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\');
        }

        if (!TryGetOrigin(returnUrl, out string? origin))
        {
            return false;
        }

        return allowedOrigins.Any(allowed =>
            TryGetOrigin(allowed, out string? allowedOrigin)
            && string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>So origin (scheme, host, port) thay vì so chuỗi để <c>http://localhost:3000.evil.com</c> không lọt qua.</summary>
    private static bool TryGetOrigin(string url, out string? origin)
    {
        origin = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
