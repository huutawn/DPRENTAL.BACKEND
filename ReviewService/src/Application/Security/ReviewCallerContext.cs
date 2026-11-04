using System.Security.Claims;
using Dprental.ReviewService.Application.Services;

namespace Dprental.ReviewService.Application.Security;

/// <summary>
/// Triển khai ngữ cảnh người gọi trích xuất từ ClaimsPrincipal.
/// </summary>
public sealed class ReviewCallerContext : IReviewCallerContext
{
    public bool IsAuthenticated { get; }
    public Guid? ActorId { get; }
    public string? UserType { get; }
    public bool IsAdmin { get; }

    public ReviewCallerContext(bool isAuthenticated, Guid? actorId, string? userType, bool isAdmin)
    {
        IsAuthenticated = isAuthenticated;
        ActorId = actorId;
        UserType = userType;
        IsAdmin = isAdmin;
    }

    /// <summary>
    /// Ngữ cảnh cho người dùng ẩn danh (chưa đăng nhập).
    /// </summary>
    public static ReviewCallerContext Anonymous => new(false, null, null, false);

    /// <summary>
    /// Dựng ngữ cảnh người gọi từ ClaimsPrincipal của HTTP request.
    /// </summary>
    public static ReviewCallerContext FromClaimsPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return Anonymous;
        }

        string? sub = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        string? userType = principal.FindFirst("u")?.Value;

        bool isAdmin = principal.IsInRole("Admin")
            || principal.Claims.Any(c => (c.Type == "r" || c.Type == ClaimTypes.Role) && string.Equals(c.Value, "Admin", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var actorId) || actorId == Guid.Empty)
        {
            return new ReviewCallerContext(true, null, userType, isAdmin);
        }

        return new ReviewCallerContext(true, actorId, userType, isAdmin);
    }

    /// <inheritdoc />
    public ReviewActorContext ToActorContext()
    {
        return new ReviewActorContext(ActorId ?? Guid.Empty, UserType, IsAdmin);
    }
}
