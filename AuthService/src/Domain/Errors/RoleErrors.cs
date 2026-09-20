using Dprental.AuthService.Common;

namespace Dprental.AuthService.Domain.Errors;

public static class RoleErrors
{
    public static readonly Error RoleNotFound =
        Error.NotFound("Role.NotFound", "Role không tồn tại.");

    public static readonly Error CannotSelfAssign =
        Error.Forbidden("Role.CannotSelfAssign", "Không thể tự gán hoặc tự thu hồi role của chính mình.");

    public static readonly Error InsufficientPermission =
        Error.Forbidden("Role.InsufficientPermission", "Bạn không có đủ quyền để gán role này — chỉ có thể gán role mang permission mà bạn đang có.");

    public static readonly Error AlreadyAssigned =
        Error.Conflict("Role.AlreadyAssigned", "User đã có role này.");

    public static readonly Error NotAssigned =
        Error.NotFound("Role.NotAssigned", "User chưa có role này.");

    public static readonly Error AssignmentFailed =
        Error.Failure("Role.AssignmentFailed", "Không thể gán role — user hoặc role không tồn tại.");
}
