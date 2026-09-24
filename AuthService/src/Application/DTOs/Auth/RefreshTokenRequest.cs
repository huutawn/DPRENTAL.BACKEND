using System.ComponentModel.DataAnnotations;

namespace Dprental.AuthService.Domain.DTOs.Auth;

/// <summary>Request chứa refresh token do client đã nhận từ login hoặc refresh trước đó.</summary>
/// <param name="RefreshToken">Refresh token bí mật; không được ghi log hoặc lưu ở dạng thô.</param>
public sealed record RefreshTokenRequest([Required] string RefreshToken);
