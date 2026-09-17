namespace Dprental.AuthService.Domain.DTOs.Auth;

/// <summary>Cặp token mà client dùng để gọi API và gia hạn phiên đăng nhập.</summary>
/// <param name="AccessToken">JWT thời hạn ngắn dùng trong header Authorization.</param>
/// <param name="RefreshToken">Token bí mật dùng một lần để lấy cặp token tiếp theo.</param>
public sealed record AuthTokenResponse(string AccessToken, string RefreshToken);
