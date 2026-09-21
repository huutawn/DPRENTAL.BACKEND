namespace Dprental.AuthService.Application.Security;

/// <summary>Mã quyền ổn định dùng trong policy — đổi tên hiển thị của permission không ảnh hưởng các hằng số này.</summary>
public static class Permissions
{
    public static class Roles
    {
        public const string Assign = "roles.assign";
        public const string Revoke = "roles.revoke";
    }
    public static class Organizations
    {
        public const string Create = "organizations.create";
        public const string Read = "organizations.read";
        public const string Update = "organizations.update";

        public static class Members
        {
            public const string Read = "organizations.members.read";
            public const string Manage = "organizations.members.manage";
        }
    }

    public static class Platform
    {
        public const string Manage = "platform.manage";
    }


    /// <summary>Toàn bộ mã quyền đã khai báo — dùng để đăng ký policy hàng loạt, tránh quên khi thêm quyền mới.</summary>
    public static IReadOnlyCollection<string> All { get; } =
    [
        Roles.Assign,
        Roles.Revoke,
        Organizations.Create,
        Organizations.Read,
        Organizations.Update,
        Organizations.Members.Read,
        Organizations.Members.Manage,
        Platform.Manage
    ];
}
