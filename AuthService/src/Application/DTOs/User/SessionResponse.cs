namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Thông tin về một phiên đăng nhập của người dùng.</summary>
public record SessionResponse(
    Guid Id,
    string? DeviceName,
    string? IpAddress,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    bool IsActive,
    bool IsCurrent
);