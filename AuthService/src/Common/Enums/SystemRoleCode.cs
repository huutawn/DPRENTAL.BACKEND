namespace Dprental.AuthService.Common.Enums;

/// <summary>Mã role hệ thống chuẩn được hỗ trợ.</summary>
public static class SystemRoleCode
{
    public const string Admin = "admin";
    public const string AdminPlatform = "admin_platform";
    public const string Owner = "Owner";
    public const string Member = "Member";

    /// <summary>Role toàn cục gán mặc định cho mọi user đăng ký qua /register — không mang permission nào.</summary>
    public const string Customer = "customer";

    public static readonly IReadOnlyCollection<string> All =
    [
        Admin,
        AdminPlatform,
        Owner,
        Member,
        Customer
    ];
}
