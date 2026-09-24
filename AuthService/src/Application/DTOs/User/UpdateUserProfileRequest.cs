namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record UpdateUserProfileRequest(
    string FullName,
    string? PhoneNumber,
    string? AvatarUrl
);