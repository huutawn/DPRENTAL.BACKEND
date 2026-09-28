using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.User;

namespace Dprental.AuthService.Application.IServices;

public interface IUserService
{
    Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<Result<AccountCreatedResponse>> RegisterAdminAsync(RegisterAdminRequest request, string? creationKey, CancellationToken cancellationToken = default);
    Task<Result> UpdateUserProfileAsync(Guid userId, UpdateUserProfileRequest request, CancellationToken cancellationToken = default);
    Task<Result<AdminUserDetailDto>> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Result> SuspendUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default);
    Task<Result> ActivateUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default);
    Task<Result<Guid>> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<Result> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken = default);
    Task<Result> ResetPasswordAsync(string token, ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
    Task<Result<PagedResult<AdminUserListItemDto>>> GetUsersPagedAsync(PageRequest pageRequest, CancellationToken cancellationToken = default);

    /// <summary>Kiểm tra user tồn tại và đang active (dùng cho JWT Kill Switch).</summary>
    Task<Result> ValidateActiveUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
