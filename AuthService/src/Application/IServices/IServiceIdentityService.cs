using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

public interface IServiceIdentityService
{
    Task<Result<ServiceTokenResponse>> IssueTokenAsync(ServiceTokenRequest request, CancellationToken cancellationToken = default);
}
