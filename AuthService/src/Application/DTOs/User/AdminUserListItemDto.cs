namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record AdminUserListItemDto(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    DateTime CreatedAtUtc,
    List<string> Roles
);