using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>
/// Đăng ký tài khoản customer. Tuổi tối thiểu 18 được kiểm tra ở service.
/// Không thu thập CCCD ở bước này — CCCD chỉ được nhập khi user đăng ký tổ chức (người đại diện pháp luật)
/// hoặc khi thuê với tư cách cá nhân (qua hoàn thiện hồ sơ, chưa xây dựng).
/// </summary>
public sealed record RegisterRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, StringLength(100)] string FullName,
    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string? PhoneNumber = null,
    DateOnly? DateOfBirth = null);

/// <summary>Tạo tài khoản quản trị qua khóa khởi tạo — không yêu cầu thông tin định danh như customer.</summary>
public sealed record RegisterAdminRequest(
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required, StringLength(100)] string FullName);

public static class RegistrationPatterns
{
    public const string CitizenId = @"^\d{12}$";

    /// <summary>Số di động/cố định Việt Nam: bắt đầu bằng 0 hoặc +84, theo sau là 9 chữ số.</summary>
    public const string PhoneNumber = @"^(0|\+84)\d{9}$";

    /// <summary>Mã số doanh nghiệp 10 số, hoặc 13 số dạng 10-3 cho chi nhánh.</summary>
    public const string TaxCode = @"^\d{10}(-\d{3})?$";
}
