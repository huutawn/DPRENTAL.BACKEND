using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Application.DTOs.Auth;

/// <summary>Request từ Next BFF sau khi Firebase SDK đăng nhập Google thành công.</summary>
public sealed record FirebaseLoginRequest(
    [Required, StringLength(4096)] string? IdToken);
