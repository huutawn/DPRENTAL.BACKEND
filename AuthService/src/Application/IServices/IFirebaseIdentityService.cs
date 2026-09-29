using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.IServices;

public interface IFirebaseIdentityService
{
    Task<Result<FirebaseIdentity>> VerifyIdTokenAsync(string? idToken, CancellationToken cancellationToken = default);
}
