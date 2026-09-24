namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Thông tin về một phiên đăng nhập của người dùng.</summary>
public sealed record SessionResponse(
    Guid Id,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    bool IsActive,
    bool IsCurrent);
