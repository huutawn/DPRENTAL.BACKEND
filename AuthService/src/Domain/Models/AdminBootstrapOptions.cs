namespace Dprental.AuthService.Domain.Models;

/// <summary>Cấu hình bootstrap khóa tạo Super Admin ban đầu.</summary>
public sealed class AdminBootstrapOptions
{
    public const string SectionName = "Auth:AdminBootstrap";

    /// <summary>Khóa bí mật dùng để xác thực quyền tạo tài khoản Super Admin.</summary>
    public string CreationKey { get; set; } = string.Empty;
}
