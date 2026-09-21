using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

public interface IUserService
{
    Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<Guid?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<Result> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken = default);
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
