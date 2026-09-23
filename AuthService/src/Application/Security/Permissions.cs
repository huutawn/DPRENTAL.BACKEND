using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Reflection.Metadata.Ecma335;

namespace Dprental.AuthService.Application.Security;

/// <summary>Mã quyền ổn định dùng trong policy — đổi tên hiển thị của permission không ảnh hưởng các hằng số này.</summary>
public static class Permissions
{
    public static class Roles
    {
        public const string Assign = "roles.assign";
        public const string Revoke = "roles.revoke";
    }

    public static class  Users
    {
        public const string Read = "identity.users.read";
        public const string Manage = "identity.users.manage";
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
