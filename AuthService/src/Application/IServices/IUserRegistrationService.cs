using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

public interface IUserRegistrationService
{
    Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
}
