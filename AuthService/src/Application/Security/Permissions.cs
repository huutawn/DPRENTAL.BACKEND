using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.Security;

/// <summary>Mã quyền ổn định dùng trong policy — ánh xạ chuẩn theo catalog PermissionCode.</summary>
public static class Permissions
{
    public static class Roles
    {
        public const string Assign = PermissionCode.IdentityRolesAssignGlobal;
        public const string Revoke = PermissionCode.IdentityRolesRevokeGlobal;
        public const string AssignGlobal = PermissionCode.IdentityRolesAssignGlobal;
        public const string RevokeGlobal = PermissionCode.IdentityRolesRevokeGlobal;
        public const string AssignOrganization = PermissionCode.IdentityRolesAssignOrganization;
        public const string RevokeOrganization = PermissionCode.IdentityRolesRevokeOrganization;
        public const string Read = PermissionCode.IdentityRolesRead;

        /// <summary>
        /// Policy gộp cho endpoint Assign dùng chung cho cả global lẫn organization: chấp nhận JWT
        /// mang alias của MỘT trong hai permission. Đây chỉ là lớp lọc thô ở tầng claim (chặn token
        /// legacy/không hợp lệ) — quyền thật theo đúng organization vẫn do RoleAssignmentService
        /// tự truy vấn DB, vì JWT gộp permission của mọi organization user thuộc về, không phân biệt
        /// đang thao tác trên organization nào.
        /// </summary>
        public const string AssignAny = "Roles.AssignAny";
    }

    public static class Organizations
    {
        public const string Create = PermissionCode.IdentityOrganizationsCreate;
        public const string Read = PermissionCode.IdentityOrganizationsRead;
        public const string Update = PermissionCode.IdentityOrganizationsUpdate;
        public const string Suspend = PermissionCode.IdentityOrganizationsSuspend;

        public static class Members
        {
            public const string Read = PermissionCode.IdentityMembersRead;
            public const string Manage = PermissionCode.IdentityMembersManage;
        }
    }

    public static class Users
    {
        public const string Read = PermissionCode.IdentityUsersRead;
        public const string Manage = PermissionCode.IdentityUsersManage;
    }



    /// <summary>Toàn bộ mã quyền đã khai báo — dùng để đăng ký policy hàng loạt, tránh quên khi thêm quyền mới.</summary>
    public static IReadOnlyCollection<string> All { get; } =
    [
        Roles.Assign,
        Roles.Revoke,
        Users.Read,
        Users.Manage
    ];
}