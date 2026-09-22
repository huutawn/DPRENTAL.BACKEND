using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.DTOs.User;

namespace Dprental.AuthService.Application.IServices;

public interface IUserService
{
    Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<Result<AccountCreatedResponse>> RegisterAdminAsync(RegisterAdminRequest request, string? creationKey, CancellationToken cancellationToken = default);
    Task<Guid?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<Result> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken = default);
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
