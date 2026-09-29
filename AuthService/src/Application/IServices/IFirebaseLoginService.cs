using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.IServices;

public interface IFirebaseLoginService
{
    Task<Result<AuthTokenResponse>> LoginAsync(string? idToken, CancellationToken cancellationToken = default);
}
