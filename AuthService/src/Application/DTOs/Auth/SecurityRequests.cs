using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Yêu cầu đổi mật khẩu cho tài khoản đang đăng nhập.</summary>
public sealed record ChangePasswordRequest(
    [Required] string OldPassword,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword);

/// <summary>Yêu cầu đặt lại mật khẩu bằng token khôi phục.</summary>
public sealed record ResetPasswordRequest(
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword,
    [Required]
    [property: Compare("NewPassword", ErrorMessage = "Mật khẩu xác nhận không khớp.")]
    string ConfirmNewPassword);

/// <summary>Yêu cầu gửi email khôi phục mật khẩu.</summary>
public sealed record RequestPasswordResetRequest(
    [Required, EmailAddress, StringLength(254)] string Email);


/// <summary>Yêu cầu xác thực mã OTP đặt lại mật khẩu.</summary>
public sealed record VerifyResetOtpRequest(
    [Required(ErrorMessage = "Mã OTP là bắt buộc."), StringLength(10, MinimumLength = 6, ErrorMessage = "Mã OTP phải từ 6 đến 10 ký tự.")] string Otp,
    [EmailAddress, StringLength(254)] string? Email = null);
/// <summary>Yêu cầu thu hồi một phiên đăng nhập cụ thể.</summary>
public sealed record RevokeDeviceSessionRequest(
    [Required(ErrorMessage = "SessionId là bắt buộc.")] Guid SessionId);
