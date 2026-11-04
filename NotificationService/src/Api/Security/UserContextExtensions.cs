using System.Security.Claims;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Api.Security;

public static class UserContextExtensions
{
    public static Result<Guid> TryGetHumanUserId(this ClaimsPrincipal principal)
    {
        if (principal?.Identity == null || !principal.Identity.IsAuthenticated)
        {
            return Result.Failure<Guid>(
                Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa xác thực hoặc thiếu Bearer token."));
        }

        // Kiểm tra xem có phải service token không: u: "svc"
        var isServiceToken = string.Equals(principal.FindFirst("u")?.Value, "svc", StringComparison.OrdinalIgnoreCase) ||
                             principal.Claims.Any(c => string.Equals(c.Type, "u", StringComparison.OrdinalIgnoreCase) &&
                                                       string.Equals(c.Value, "svc", StringComparison.OrdinalIgnoreCase));
        if (isServiceToken)
        {
            return Result.Failure<Guid>(
                Error.Forbidden("SERVICE_TOKEN_FORBIDDEN", "Service principal (u: svc) không được phép truy cập hộp thư thông báo người dùng."));
        }

        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var userId) || userId == Guid.Empty)
        {
            return Result.Failure<Guid>(
                Error.Unauthorized("INVALID_TOKEN_SUB", "Token thiếu claim 'sub' định danh người dùng hợp lệ."));
        }

        return Result.Success(userId);
    }
}
