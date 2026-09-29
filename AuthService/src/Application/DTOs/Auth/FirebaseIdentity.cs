namespace Dprental.AuthService.Application.DTOs.Auth;

public sealed record FirebaseIdentity(
    string Uid,
    string ProviderSubject,
    string? SignInProvider,
    string Email,
    bool EmailVerified,
    string? DisplayName,
    string? PhotoUrl);
