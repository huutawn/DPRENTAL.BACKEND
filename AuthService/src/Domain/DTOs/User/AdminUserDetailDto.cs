namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record AdminUserDetailDto(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    string? AvatarUrl,
    bool IsActive,
    DateTime CreatedAtUtc,
    IEnumerable<string> GlobalRoles
);