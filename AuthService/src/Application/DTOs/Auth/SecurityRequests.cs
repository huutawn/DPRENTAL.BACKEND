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
    string ConfirmNewPassword
);

/// <summary>Yêu cầu gửi email khôi phục mật khẩu.</summary>
public sealed record RequestPasswordResetRequest(string Email);