using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Services;

public sealed class FirebaseLoginService(
    IFirebaseIdentityService firebaseIdentityService,
    IExternalLoginService externalLoginService,
    IAuthSessionService authSessionService) : IFirebaseLoginService
{
    private const string GoogleProvider = "Google";
    private const string FirebaseGoogleSignInProvider = "google.com";

    public async Task<Result<AuthTokenResponse>> LoginAsync(
        string? idToken,
        CancellationToken cancellationToken = default)
    {
        Result<FirebaseIdentity> firebaseResult = await firebaseIdentityService.VerifyIdTokenAsync(
            idToken,
            cancellationToken);
        if (firebaseResult.IsFailure)
        {
            return Result.Failure<AuthTokenResponse>(firebaseResult.Error);
        }

        FirebaseIdentity firebaseIdentity = firebaseResult.Value;
        if (!string.Equals(firebaseIdentity.SignInProvider, FirebaseGoogleSignInProvider, StringComparison.Ordinal))
        {
            return Result.Failure<AuthTokenResponse>(AuthErrors.Auth.FirebaseProviderUnsupported);
        }

        ExternalIdentity externalIdentity = new(
            GoogleProvider,
            firebaseIdentity.ProviderSubject,
            firebaseIdentity.Email,
            firebaseIdentity.EmailVerified,
            firebaseIdentity.DisplayName);

        Result<Guid> signInResult = await externalLoginService.SignInAsync(externalIdentity, cancellationToken);
        if (signInResult.IsFailure)
        {
            return Result.Failure<AuthTokenResponse>(signInResult.Error);
        }

        Result<AuthTokenPair> sessionResult = await authSessionService.CreateSessionAsync(
            signInResult.Value,
            cancellationToken);
        if (sessionResult.IsFailure)
        {
            return Result.Failure<AuthTokenResponse>(sessionResult.Error);
        }

        return Result.Success(new AuthTokenResponse(
            sessionResult.Value.AccessToken,
            sessionResult.Value.RefreshToken));
    }
}
