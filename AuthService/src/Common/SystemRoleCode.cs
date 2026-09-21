namespace Dprental.AuthService.Common;

/// <summary>Mã role hệ thống chuẩn được hỗ trợ.</summary>
public static class SystemRoleCode
{
    public const string Admin = "admin";
    public const string AdminPlatform = "admin_platform";
    public const string Owner = "Owner";
    public const string Member = "Member";

    public static readonly IReadOnlyCollection<string> All =
    [
        Admin,
        AdminPlatform,
        Owner,
        Member
    ];
}
