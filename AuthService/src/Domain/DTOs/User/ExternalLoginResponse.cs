namespace Dprental.AuthService.Domain.DTOs.User;

/// <summary>Thông tin về tài khoản liên kết bên ngoài (Google,...) của người dùng.</summary>
public sealed record ExternalLoginResponse(
    string Provider,
    string? Email,
    DateTime LinkedAtUtc);
