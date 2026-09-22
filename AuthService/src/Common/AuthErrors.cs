namespace Dprental.AuthService.Common;

/// <summary>Catalog lỗi tập trung authoritative của AuthService, đảm bảo toàn bộ title, detail và HTTP status đồng nhất.</summary>
public static class AuthErrors
{
    public static class Organization
    {
        public static readonly Error NotFound =
            Error.NotFound(AuthErrorCode.Organization.NotFound, "Organization không tồn tại.");
        public static readonly Error OrganizationNotFound = NotFound;

        public static readonly Error Inactive =
            Error.Forbidden(AuthErrorCode.Organization.Inactive, "Organization đã bị vô hiệu hóa.");
        public static readonly Error InactiveOrganization = Inactive;

        public static readonly Error CrossOrganizationAccessDenied =
            Error.Forbidden(AuthErrorCode.Organization.CrossOrganizationAccessDenied, "Bạn không có quyền truy cập organization này.");

        public static readonly Error Forbidden =
            Error.Forbidden(AuthErrorCode.Organization.Forbidden, "Bạn không có đủ quyền thực hiện thao tác này.");

        public static readonly Error SlugAlreadyExists =
            Error.Conflict(AuthErrorCode.Organization.SlugAlreadyExists, "Slug của organization đã tồn tại.");

        public static readonly Error MembershipAlreadyExists =
            Error.Conflict(AuthErrorCode.Organization.MembershipAlreadyExists, "User đã là thành viên của organization này.");

        public static readonly Error MembershipNotFound =
            Error.NotFound(AuthErrorCode.Organization.MembershipNotFound, "Thành viên không tồn tại trong organization.");

        public static readonly Error UserNotFound =
            Error.NotFound(AuthErrorCode.Organization.UserNotFound, "Người dùng không tồn tại.");

        public static readonly Error UserInactive =
            Error.Validation(AuthErrorCode.Organization.UserInactive, "Người dùng đang bị khóa.");

        public static readonly Error RoleNotFound =
            Error.NotFound(AuthErrorCode.Organization.RoleNotFound, "Role không tồn tại.");

        public static readonly Error InvalidRoleScope =
            Error.Validation(AuthErrorCode.Organization.InvalidRoleScope, "Chỉ có thể gán role thuộc phạm vi Organization.");

        public static readonly Error PrivilegeEscalation =
            Error.Forbidden(AuthErrorCode.Organization.PrivilegeEscalation, "Không thể gán role mang quyền hạn vượt quá quyền hạn của bạn.");

        public static readonly Error LastOwner =
            Error.Validation(AuthErrorCode.Organization.LastOwner, "Không thể xóa, đình chỉ hoặc hạ quyền Owner cuối cùng của organization.");

        public static readonly Error InvalidStateTransition =
            Error.Validation(AuthErrorCode.Organization.InvalidStateTransition, "Chuyển đổi trạng thái không hợp lệ.");

        public static readonly Error CannotActivateInvitation =
            Error.Validation(AuthErrorCode.Organization.CannotActivateInvitation, "Chỉ người được mời mới có thể tự kích hoạt lời mời qua endpoint accept.");

        public static readonly Error EmptyPatch =
            Error.Validation(AuthErrorCode.Organization.EmptyPatch, "Yêu cầu cập nhật không chứa trường hợp lệ nào.");

        public static readonly Error NoOpPatch =
            Error.Validation(AuthErrorCode.Organization.NoOpPatch, "Dữ liệu cập nhật không có sự thay đổi.");

        public static readonly Error InvalidSlugFormat =
            Error.Validation(AuthErrorCode.Organization.InvalidSlugFormat, "Slug không đúng định dạng URL-safe canonical.");

        public static readonly Error InvalidName =
            Error.Validation(AuthErrorCode.Organization.InvalidName, "Tên organization không hợp lệ.");

        public static readonly Error PlatformAdminRequired =
            Error.Forbidden(AuthErrorCode.Organization.PlatformAdminRequired, "Chỉ quản trị viên nền tảng mới có quyền thực hiện thao tác này.");

        public static readonly Error CreationNotAllowed =
            Error.Forbidden(AuthErrorCode.Organization.CreationNotAllowed, "Bạn không có quyền tạo organization.");
    }

    public static class Role
    {
        public static readonly Error NotFound =
            Error.NotFound(AuthErrorCode.Role.NotFound, "Role không tồn tại.");
        public static readonly Error RoleNotFound = NotFound;

        public static readonly Error CannotSelfAssign =
            Error.Forbidden(AuthErrorCode.Role.CannotSelfAssign, "Không thể tự gán hoặc tự thu hồi role của chính mình.");

        public static readonly Error InsufficientPermission =
            Error.Forbidden(AuthErrorCode.Role.InsufficientPermission, "Bạn không có đủ quyền để gán role này — chỉ có thể gán role mang permission mà bạn đang có.");

        public static readonly Error AlreadyAssigned =
            Error.Conflict(AuthErrorCode.Role.AlreadyAssigned, "User đã có role này.");

        public static readonly Error NotAssigned =
            Error.NotFound(AuthErrorCode.Role.NotAssigned, "User chưa có role này.");

        public static readonly Error AssignmentFailed =
            Error.Failure(AuthErrorCode.Role.AssignmentFailed, "Không thể gán role — user hoặc role không tồn tại.");

        public static readonly Error InvalidScope =
            Error.Validation(AuthErrorCode.Role.InvalidScope, "Chỉ có thể gán role toàn cục qua endpoint này.");

        public static readonly Error OrganizationRequired =
            Error.Validation(AuthErrorCode.Role.OrganizationRequired, "Role thuộc phạm vi Organization — cần cung cấp OrganizationId.");

        public static readonly Error OrganizationRevokeNotSupported =
            Error.Validation(AuthErrorCode.Role.OrganizationRevokeNotSupported, "Không thể thu hồi role theo Organization qua endpoint này — dùng DELETE /organizations/{id}/members/{memberId}.");
    }

    public static class User
    {
        public static readonly Error EmailAlreadyExists =
            Error.Conflict(AuthErrorCode.User.EmailAlreadyExists, "Email đã được sử dụng.");

        public static readonly Error NotFound =
            Error.NotFound(AuthErrorCode.User.NotFound, "Người dùng không tồn tại hoặc tài khoản đã bị khóa.");
        public static readonly Error UserNotFound = NotFound;
    }

    public static class Navigation
    {
        public static readonly Error InvalidOrganizationContext =
            Error.Validation(AuthErrorCode.Navigation.InvalidOrganizationContext, "Organization context không hợp lệ.");
    }

    public static class ServicePrincipal
    {
        public static readonly Error InvalidClient =
            Error.Unauthorized(AuthErrorCode.ServicePrincipal.InvalidClient, "client_id hoặc client_secret không hợp lệ.");

        public static readonly Error InvalidScope =
            Error.Forbidden(AuthErrorCode.ServicePrincipal.InvalidScope, "Service này không được cấp scope/audience đã yêu cầu.");
    }

    public static class ExternalLogin
    {
        public static readonly Error MissingEmail =
            Error.Validation(AuthErrorCode.ExternalLogin.MissingEmail, "Tài khoản bên ngoài không cung cấp email.");

        public static readonly Error EmailNotVerified =
            Error.Forbidden(AuthErrorCode.ExternalLogin.EmailNotVerified, "Email của tài khoản bên ngoài chưa được xác minh.");

        public static readonly Error AccountDisabled =
            Error.Forbidden(AuthErrorCode.ExternalLogin.AccountDisabled, "Tài khoản không tồn tại hoặc đã bị khóa.");

        public static readonly Error AlreadyLinked =
            Error.Conflict(AuthErrorCode.ExternalLogin.AlreadyLinked, "Tài khoản này đã liên kết với một tài khoản bên ngoài khác.");
    }

    public static class Auth
    {
        public static readonly Error InvalidCredentials =
            Error.Unauthorized(AuthErrorCode.Auth.InvalidCredentials, "Email hoặc mật khẩu không chính xác.");

        public static readonly Error InactiveAccount =
            Error.Unauthorized(AuthErrorCode.Auth.InactiveAccount, "Tài khoản không tồn tại hoặc đã bị khóa.");

        public static readonly Error MissingCookie =
            Error.Unauthorized(AuthErrorCode.Auth.MissingCookie, "Không tìm thấy refresh token trong cookie.");

        public static readonly Error InvalidRefreshToken =
            Error.Unauthorized(AuthErrorCode.Auth.InvalidRefreshToken, "Refresh token không hợp lệ hoặc đã hết hạn.");
    }

    public static class Paging
    {
        public static readonly Error InvalidPage =
            Error.Validation(AuthErrorCode.Paging.InvalidPage, "Số trang phải lớn hơn 0.");

        public static readonly Error InvalidPageSize =
            Error.Validation(AuthErrorCode.Paging.InvalidPageSize, "Kích thước trang phải từ 1 đến 100.");
    }

    public static class AdminBootstrap
    {
        public static readonly Error InvalidKey =
            Error.Unauthorized(AuthErrorCode.AdminBootstrap.InvalidKey, "Khóa khởi tạo quản trị viên không hợp lệ.");
    }
}
