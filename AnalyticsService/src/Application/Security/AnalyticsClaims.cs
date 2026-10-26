using System.Security.Claims;

namespace Dprental.AnalyticsService.Application.Security;

/// <summary>
/// Đọc claim từ JWT của AuthService, theo đúng quy ước PaymentClaimsPrincipalExtensions:
/// "u" = "svc" là token service; vai trò admin nằm ở claim "r"/"role"; danh tính là "sub" (Guid).
/// </summary>
public static class AnalyticsClaims
{
    private static readonly string[] AdminRoles = ["adm", "adp", "admin", "admin_platform"];

    public static bool IsServiceToken(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.Equals(user.FindFirst("u")?.Value, "svc", StringComparison.Ordinal);
    }

    public static bool IsPlatformAdmin(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.IsServiceToken())
        {
            return false;
        }

        return user.FindAll("r").Concat(user.FindAll("role"))
            .Any(claim => AdminRoles.Contains(claim.Value, StringComparer.Ordinal));
    }

    public static bool HasAudience(this ClaimsPrincipal user, string audience)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindAll("aud").Any(claim => string.Equals(claim.Value, audience, StringComparison.Ordinal));
    }

    public static bool TryGetSubjectUserId(this ClaimsPrincipal user, out Guid userId)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out userId) && userId != Guid.Empty;
    }

    /// <summary>
    /// Người dùng thật (không phải service), có audience client và có sub hợp lệ.
    /// Dùng chung cho policy của Hub và UserIdProvider để hai nơi không lệch nhau.
    /// </summary>
    public static bool IsHubClient(this ClaimsPrincipal user, string clientAudience) =>
        user.Identity is { IsAuthenticated: true }
        && !user.IsServiceToken()
        && user.HasAudience(clientAudience)
        && user.TryGetSubjectUserId(out _);
}
