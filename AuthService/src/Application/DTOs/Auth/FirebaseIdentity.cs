namespace Dprental.AuthService.Application.DTOs.Auth;

public sealed record FirebaseIdentity(
    string Uid,
    string Email,
    bool EmailVerified,
    string? DisplayName,
    string? PhotoUrl);
