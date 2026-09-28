using AuthService.Application.DTOs.User;
using AuthService.Domain.DTOs.User;
using Dprental.AuthService.Application.DTOs;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Dprental.AuthService.Application.Services;

public sealed class UserService(
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    IPasswordHasher<User> passwordHasher,
    IOptions<AdminBootstrapOptions> adminBootstrapOptions,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository,
    IAuthSessionRepository authSessionRepository,
    IRecoveryTokenRepository recoveryTokenRepository,
    ILogger<UserService> logger) : IUserService
{
    public async Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<AccountCreatedResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "Register",
            async () =>
            {
                string email = NormalizeEmail(request.Email);
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (await CustomerAccountRules.ValidateAsync(request, email, userRepository, now, cancellationToken) is Error error)
                {
                    return Result.Failure<AccountCreatedResponse>(error);
                }

                User user = CustomerAccountRules.Create(request, email, passwordHasher, now.UtcDateTime);
                Role? customerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Customer, cancellationToken);

                if (customerRole is not null)
                {
                    await userRepository.CreateWithRoleAsync(user, customerRole.Id, cancellationToken);
                }
                else
                {
                    await userRepository.CreateAsync(user, cancellationToken);
                }

                return Result.Success(new AccountCreatedResponse(user.Id));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }



    public async Task<Result<AccountCreatedResponse>> RegisterAdminAsync(
        RegisterAdminRequest request,
        string? creationKey,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<AccountCreatedResponse>(validationError);
        }

        if (!IsAdminCreationKeyValid(creationKey))
        {
            return Result.Failure<AccountCreatedResponse>(AuthErrors.AdminBootstrap.InvalidKey);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RegisterAdmin",
            async () =>
            {
                string email = NormalizeEmail(request.Email);
                if (await userRepository.EmailExistsAsync(email, cancellationToken))
                {
                    return Result.Failure<AccountCreatedResponse>(AuthErrors.User.EmailAlreadyExists);
                }

                Role? adminRole = await roleRepository.GetByNameAsync(SystemRoleCode.Admin, cancellationToken);
                if (adminRole is null)
                {
                    return Result.Failure<AccountCreatedResponse>(AuthErrors.Role.RoleNotFound);
                }

                User user = CreateUserEntity(request, email);
                await userRepository.CreateWithRoleAsync(user, adminRole.Id, cancellationToken);

                return Result.Success(new AccountCreatedResponse(user.Id));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<Guid>> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<Guid>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "Authenticate",
            async () =>
            {
                string email = NormalizeEmail(request.Email);
                User? user = await userRepository.FindByEmailAsync(email, cancellationToken);
                string corrId = correlationIdProvider.GetCorrelationId();

                // 1. Kiểm tra tài khoản tồn tại (không tiết lộ email tồn tại -> AuthErrors.Auth.InvalidCredentials)
                if (user is null)
                {
                    await LogAuditAsync(null, email, "Login", "Failed_UserNotFound", corrId, cancellationToken);
                    return Result.Failure<Guid>(AuthErrors.Auth.InvalidCredentials);
                }

                // 2. Kiểm tra trạng thái kích hoạt (không tiết lộ -> AuthErrors.Auth.InvalidCredentials)
                if (!user.IsActive)
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_AccountDisabled", corrId, cancellationToken);
                    return Result.Failure<Guid>(AuthErrors.Auth.InvalidCredentials);
                }

                // 3. Kiểm tra trạng thái khóa tài khoản (Lockout)
                if (user.LockoutEnd is not null && user.LockoutEnd > timeProvider.GetUtcNow())
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_LockedOut", corrId, cancellationToken);
                    return Result.Failure<Guid>(AuthErrors.Auth.InvalidCredentials);
                }

                // 4. Xác thực mật khẩu
                PasswordVerificationResult verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
                if (verification is not PasswordVerificationResult.Success and not PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.AccessFailedCount++;

                    // Khóa 15 phút nếu sai 5 lần
                    if (user.AccessFailedCount >= 5)
                    {
                        user.LockoutEnd = timeProvider.GetUtcNow().AddMinutes(15);
                    }

                    await userRepository.UpdateAsync(user, cancellationToken);
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_InvalidCredentials", corrId, cancellationToken);
                    return Result.Failure<Guid>(AuthErrors.Auth.InvalidCredentials);
                }

                // 5. Cập nhật lại Hash nếu thuật toán mã hóa thay đổi
                if (verification == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
                }

                // 6. Đăng nhập thành công -> Reset bộ đếm lỗi và thời gian khóa
                user.AccessFailedCount = 0;
                user.LockoutEnd = null;
                await userRepository.UpdateAsync(user, cancellationToken);

                await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Success", corrId, cancellationToken);
                return Result.Success(user.Id);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> ValidateActiveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ValidateActiveUser",
            async () =>
            {
                bool isActive = await userRepository.IsUserActiveAsync(userId, cancellationToken);
                if (!isActive)
                {
                    return Result.Failure(AuthErrors.Auth.InactiveAccount);
                }

                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RequestPasswordReset",
            async () =>
            {
                string email = NormalizeEmail(request.Email);
                string corrId = correlationIdProvider.GetCorrelationId();
                User? user = await userRepository.FindByEmailAsync(email, cancellationToken);

                // Luôn trả Success để không tiết lộ email có tồn tại hay không
                if (user is null)
                {
                    await LogAuditAsync(null, email, "Password_ResetRequest", "Success_NoUser", corrId, cancellationToken);
                    return Result.Success();
                }

                if (!user.IsActive)
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Failed_AccountDisabled", corrId, cancellationToken);
                    return Result.Success();
                }

                string rawToken = RefreshTokenUtility.Create();

                RecoveryToken recoveryToken = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = RefreshTokenUtility.ComputeHash(rawToken),
                    Purpose = "ResetPassword",
                    CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
                    ExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(30)
                };

                await recoveryTokenRepository.AddAsync(recoveryToken, cancellationToken);
                await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Success", corrId, cancellationToken);

                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> ResetPasswordAsync(string token, ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã khôi phục không hợp lệ."));
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ResetPassword",
            async () =>
            {
                string corrId = correlationIdProvider.GetCorrelationId();

                string incomingTokenHash = RefreshTokenUtility.ComputeHash(token);
                RecoveryToken? recoveryToken = await recoveryTokenRepository.FindByHashAsync(incomingTokenHash, cancellationToken);

                if (recoveryToken is null ||
                    recoveryToken.Purpose != "ResetPassword" ||
                    recoveryToken.UsedAtUtc is not null)
                {
                    await LogAuditAsync(null, "Unknown", "Password_Reset", "Failed_InvalidOrReusedToken", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã khôi phục không hợp lệ hoặc đã được sử dụng."));
                }

                if (recoveryToken.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
                {
                    await LogAuditAsync(recoveryToken.UserId, recoveryToken.UserId.ToString(), "Password_Reset", "Failed_TokenExpired", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.TokenExpired", "Mã khôi phục đã hết hạn."));
                }

                User? user = await userRepository.GetByIdAsync(recoveryToken.UserId, cancellationToken);
                if (user is null)
                {
                    await LogAuditAsync(recoveryToken.UserId, recoveryToken.UserId.ToString(), "Password_Reset", "Failed_UserNotFound", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã không hợp lệ hoặc tài khoản không tồn tại."));
                }

                string newPasswordHash = passwordHasher.HashPassword(user, request.NewPassword);

                // Transaction nguyên tử: tiêu thụ token (conditional update) + cập nhật mật khẩu + thu hồi mọi session
                bool resetSucceeded = await userRepository.ResetPasswordWithTokenAsync(
                    user.Id,
                    incomingTokenHash,
                    newPasswordHash,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);

                if (!resetSucceeded)
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Failed_InvalidOrReusedToken", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã khôi phục không hợp lệ hoặc đã được sử dụng."));
                }

                await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Success", corrId, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? userError))
        {
            return Result.Failure(userError);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ChangePassword",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
                string corrId = correlationIdProvider.GetCorrelationId();

                if (user is null)
                {
                    await LogAuditAsync(null, userId.ToString(), "Password_Change", "Failed_UserNotFound", corrId, cancellationToken);
                    return Result.Failure(Error.NotFound("Auth.UserNotFound", "Người dùng không tồn tại."));
                }

                if (!user.IsActive)
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Change", "Failed_AccountDisabled", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.AccountDisabled", "Tài khoản đã bị khóa."));
                }

                PasswordVerificationResult verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.OldPassword);
                if (verification is not PasswordVerificationResult.Success and not PasswordVerificationResult.SuccessRehashNeeded)
                {
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Change", "Failed_InvalidOldPassword", corrId, cancellationToken);
                    return Result.Failure(Error.Unauthorized("Auth.InvalidPassword", "Mật khẩu hiện tại không chính xác."));
                }

                string newPasswordHash = passwordHasher.HashPassword(user, request.NewPassword);

                // Transaction nguyên tử: đổi mật khẩu và thu hồi toàn bộ session
                await userRepository.ChangePasswordAndRevokeSessionsAsync(
                    user.Id,
                    newPasswordHash,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);

                await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Change", "Success", corrId, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<CurrentUserResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetCurrentUserProfile",
            async () =>
            {
                User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
                }

                return Result.Success(user.ToCurrentUserResponse());
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> UpdateUserProfileAsync(Guid userId, UpdateUserProfileRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? userError))
        {
            return Result.Failure(userError);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "UpdateUserProfile",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure(AuthErrors.User.NotFound);
                }

                user.FullName = request.FullName.Trim();
                user.PhoneNumber = request.PhoneNumber?.Trim();
                user.AvatarUrl = request.AvatarUrl?.Trim();

                await userRepository.UpdateAsync(user, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<AdminUserDetailDto>> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<AdminUserDetailDto>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetUserById",
            async () =>
            {
                User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure<AdminUserDetailDto>(AuthErrors.User.NotFound);
                }

                var detailDto = new AdminUserDetailDto(
                    user.Id,
                    user.Email,
                    user.FullName,
                    user.PhoneNumber,
                    user.AvatarUrl,
                    user.IsActive,
                    user.CreatedAt,
                    UserAccessResolver.Roles(user)
                );

                return Result.Success(detailDto);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<PagedResult<AdminUserListItemDto>>> GetUsersPagedAsync(GetUsersRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Result.Failure<PagedResult<AdminUserListItemDto>>(AuthErrors.Paging.InvalidPage);
        }

        // Tái sử dụng PageRequest để chạy logic Validate() có sẵn của bạn
        var pageRequest = new PageRequest(request.Page, request.PageSize, request.Search);
        Result validation = pageRequest.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<AdminUserListItemDto>>(validation.Error);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetUsersPaged",
            async () =>
            {
                int skip = (request.Page - 1) * request.PageSize;

                // Bổ sung tham số request.Type vào hàm của Repository
                var (users, totalCount) = await userRepository.GetUsersPagedAsync(
                    skip,
                    request.PageSize,
                    pageRequest.GetSearchTerm(),
                    request.Type, // <-- Đẩy filter Type xuống Repository
                    cancellationToken);

                int totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

                // Cập nhật mapping: Thêm tham số danh sách Role vào cuối constructor
                var items = users.Select(u => new AdminUserListItemDto(
                    u.Id,
                    u.Email,
                    u.FullName,
                    u.IsActive,
                    u.CreatedAt,
                    u.UserRoles?.Select(ur => ur.Role.Name).ToList() ?? [] // <-- Trích xuất danh sách Role
                )).ToList();

                return Result.Success(new PagedResult<AdminUserListItemDto>(items, request.Page, request.PageSize, totalCount, totalPages));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        return await ApplicationExecutor.ExecuteAsync(
            "CreateUser",
            async () =>
            {
                // Kiểm tra email tồn tại
                bool emailExists = await userRepository.EmailExistsAsync(request.Email.ToUpperInvariant(), cancellationToken);
                if (emailExists)
                {
                    return Result.Failure<Guid>(new Error("User.EmailExists", "Email này đã được sử dụng.", ErrorType.Conflict));
                }

                var newUser = new User
                {
                    Id = Guid.NewGuid(),
                    Email = request.Email,
                    FullName = request.FullName,
                    IsActive = true,
                    CreatedAt = timeProvider.GetUtcNow().UtcDateTime, // Đồng bộ dùng timeProvider của dự án
                    HasPassword = true
                };

                // Hash mật khẩu
                newUser.PasswordHash = passwordHasher.HashPassword(newUser, request.Password);

                // Gắn Roles cho User mới
                if (request.RoleIds != null && request.RoleIds.Count != 0)
                {
                    foreach (var roleId in request.RoleIds)
                    {
                        newUser.UserRoles.Add(new UserRole { RoleId = roleId, UserId = newUser.Id });
                    }
                }

                // Gọi CreateAsync
                await userRepository.CreateAsync(newUser, cancellationToken);

                return Result.Success(newUser.Id);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        return await ApplicationExecutor.ExecuteAsync(
            "UpdateUser",
            async () =>
            {
                var user = await userRepository.GetByIdWithRolesAsync(id, cancellationToken);
                if (user is null)
                {
                    return Result.Failure(new Error("User.NotFound", "Không tìm thấy người dùng.", ErrorType.NotFound));
                }

                // Cập nhật thông tin cơ bản
                user.FullName = request.FullName;

                // Cập nhật Roles
                if (request.RoleIds != null)
                {
                    var currentRoleIds = user.UserRoles.Select(ur => ur.RoleId).ToList();

                    var rolesToRemove = user.UserRoles.Where(ur => !request.RoleIds.Contains(ur.RoleId)).ToList();
                    foreach (var role in rolesToRemove)
                    {
                        user.UserRoles.Remove(role);
                    }

                    var rolesToAdd = request.RoleIds.Where(rId => !currentRoleIds.Contains(rId)).ToList();
                    foreach (var roleId in rolesToAdd)
                    {
                        user.UserRoles.Add(new UserRole { RoleId = roleId, UserId = user.Id });
                    }
                }

                await userRepository.UpdateAsync(user, cancellationToken);

                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> SuspendUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(targetUserId, nameof(targetUserId), out Error? targetError))
        {
            return Result.Failure(targetError);
        }

        if (!DtoValidator.TryValidateId(adminId, nameof(adminId), out Error? adminError))
        {
            return Result.Failure(adminError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "SuspendUser",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(targetUserId, cancellationToken);
                string corrId = correlationIdProvider.GetCorrelationId();

                if (user is null)
                {
                    await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Failed_UserNotFound", corrId, cancellationToken);
                    return Result.Failure(AuthErrors.User.NotFound);
                }

                if (!user.IsActive)
                {
                    return Result.Success();
                }

                user.IsActive = false;
                await userRepository.UpdateAsync(user, cancellationToken);
                await authSessionRepository.RevokeAllSessionsForUserAsync(targetUserId, cancellationToken);
                await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Success", corrId, cancellationToken);

                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> ActivateUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(targetUserId, nameof(targetUserId), out Error? targetError))
        {
            return Result.Failure(targetError);
        }

        if (!DtoValidator.TryValidateId(adminId, nameof(adminId), out Error? adminError))
        {
            return Result.Failure(adminError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ActivateUser",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(targetUserId, cancellationToken);
                string corrId = correlationIdProvider.GetCorrelationId();

                if (user is null)
                {
                    await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Activate", "Failed_UserNotFound", corrId, cancellationToken);
                    return Result.Failure(AuthErrors.User.NotFound);
                }

                if (user.IsActive)
                {
                    return Result.Success();
                }

                user.IsActive = true;
                user.LockoutEnd = null;
                user.AccessFailedCount = 0;
                await userRepository.UpdateAsync(user, cancellationToken);
                await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Activate", "Success", corrId, cancellationToken);

                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    private bool IsAdminCreationKeyValid(string? providedKey)
    {
        string configuredKey = adminBootstrapOptions.Value.CreationKey;
        if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrWhiteSpace(providedKey))
        {
            return false;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        byte[] configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        return CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }

    private User CreateUserEntity(RegisterAdminRequest request, string email)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            HasPassword = true,
            IsActive = true,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        return user;
    }

    private Task LogAuditAsync(Guid? actorId, string target, string action, string result, string corrId, CancellationToken cancellationToken) =>
        auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = target,
            Action = action,
            Result = result,
            CorrelationId = corrId,
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
