using Dprental.AuthService.Common;

namespace Dprental.AuthService.Domain.Errors;

public static class OrganizationErrors
{
    public static readonly Error OrganizationNotFound =
        Error.NotFound("Organization.NotFound", "Organization không tồn tại.");

    public static readonly Error InactiveOrganization =
        Error.Forbidden("Organization.Inactive", "Organization đã bị vô hiệu hóa.");

    public static readonly Error CrossOrganizationAccessDenied =
        Error.Forbidden("Organization.CrossOrganizationAccessDenied", "Bạn không có quyền truy cập organization này.");

    public static readonly Error Forbidden =
        Error.Forbidden("Organization.Forbidden", "Bạn không có đủ quyền thực hiện thao tác này.");

    public static readonly Error SlugAlreadyExists =
        Error.Conflict("Organization.SlugAlreadyExists", "Slug của organization đã tồn tại.");

    public static readonly Error MembershipAlreadyExists =
        Error.Conflict("Organization.MembershipAlreadyExists", "User đã là thành viên của organization này.");

    public static readonly Error MembershipNotFound =
        Error.NotFound("Organization.MembershipNotFound", "Thành viên không tồn tại trong organization.");

    public static readonly Error UserNotFound =
        Error.NotFound("Organization.UserNotFound", "Người dùng không tồn tại.");

    public static readonly Error UserInactive =
        Error.Validation("Organization.UserInactive", "Người dùng đang bị khóa.");

    public static readonly Error RoleNotFound =
        Error.NotFound("Organization.RoleNotFound", "Role không tồn tại.");

    public static readonly Error InvalidRoleScope =
        Error.Validation("Organization.InvalidRoleScope", "Chỉ có thể gán role thuộc phạm vi Organization.");

    public static readonly Error PrivilegeEscalation =
        Error.Forbidden("Organization.PrivilegeEscalation", "Không thể gán role mang quyền hạn vượt quá quyền hạn của bạn.");

    public static readonly Error LastOwner =
        Error.Validation("Organization.LastOwner", "Không thể xóa, đình chỉ hoặc hạ quyền Owner cuối cùng của organization.");

    public static readonly Error InvalidStateTransition =
        Error.Validation("Organization.InvalidStateTransition", "Chuyển đổi trạng thái không hợp lệ.");

    public static readonly Error CannotActivateInvitation =
        Error.Validation("Organization.CannotActivateInvitation", "Chỉ người được mời mới có thể tự kích hoạt lời mời qua endpoint accept.");

    public static readonly Error EmptyPatch =
        Error.Validation("Organization.EmptyPatch", "Yêu cầu cập nhật không chứa trường hợp lệ nào.");

    public static readonly Error NoOpPatch =
        Error.Validation("Organization.NoOpPatch", "Dữ liệu cập nhật không có sự thay đổi.");

    public static readonly Error InvalidSlugFormat =
        Error.Validation("Organization.InvalidSlugFormat", "Slug không đúng định dạng URL-safe canonical.");

    public static readonly Error InvalidName =
        Error.Validation("Organization.InvalidName", "Tên organization không hợp lệ.");

    public static readonly Error PlatformAdminRequired =
        Error.Forbidden("Organization.PlatformAdminRequired", "Chỉ quản trị viên nền tảng mới có quyền thực hiện thao tác này.");

    public static readonly Error CreationNotAllowed =
        Error.Forbidden("Organization.CreationNotAllowed", "Bạn không có quyền tạo organization.");
}
