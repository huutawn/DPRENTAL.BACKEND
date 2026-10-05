// 1. Thư viện hệ thống cơ bản
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;

using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.ValueObjects;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;

using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Application.Security;

using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Common.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using AuthService.Domain.DTOs.User;
using AuthService.Application.DTOs.User;
using AuthService.Application.DTOs.Auth;

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
    IMediaUrlResolver mediaUrlResolver,
    IOrganizationMembershipRepository? organizationMembershipRepository = null,
    IOrganizationRepository? organizationRepository = null) : IUserService
{
    public async Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<AccountCreatedResponse>(validationError);
        }
        cancellationToken.ThrowIfCancellationRequested();


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

        string email = NormalizeEmail(request.Email);
        if (PasswordPolicy.CheckPersonalInfo(request.Password, email, request.FullName) is Error passwordError)
        {
            return Result.Failure<AccountCreatedResponse>(passwordError);
        }

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
    }

    public async Task<Result<Guid>> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<Guid>(validationError);
        }

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
    }

    public async Task<Result> ValidateActiveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        bool isActive = await userRepository.IsUserActiveAsync(userId, cancellationToken);
        if (!isActive)
        {
            return Result.Failure(AuthErrors.Auth.InactiveAccount);
        }

        return Result.Success();
    }

    public async Task<Result<string?>> RequestPasswordResetAsync(RequestPasswordResetRequest request, string? testKey, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<string?>(validationError);
        }

        string email = NormalizeEmail(request.Email);
        string corrId = correlationIdProvider.GetCorrelationId();
        User? user = await userRepository.FindByEmailAsync(email, cancellationToken);

        // Luôn trả Success để không tiết lộ email có tồn tại hay không
        if (user is null)
        {
            await LogAuditAsync(null, email, "Password_ResetRequest", "Success_NoUser", corrId, cancellationToken);
            return Result.Success<string?>(null);
        }

        if (!user.IsActive)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Failed_AccountDisabled", corrId, cancellationToken);
            return Result.Success<string?>(null);
        }

        string otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);

        RecoveryToken recoveryToken = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = RefreshTokenUtility.ComputeHash(otp),
            Purpose = "ResetPassword",
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15)
        };

        await recoveryTokenRepository.AddAsync(recoveryToken, cancellationToken);
        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Success", corrId, cancellationToken);

        return Result.Success<string?>(IsAdminCreationKeyValid(testKey) ? otp : null);
    }

    public async Task<Result<string>> VerifyResetOtpAsync(VerifyResetOtpRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<string>(validationError);
        }

        string corrId = correlationIdProvider.GetCorrelationId();
        string cleanOtp = request.Otp.Trim();
        string otpHash = RefreshTokenUtility.ComputeHash(cleanOtp);

        RecoveryToken? recoveryToken = await recoveryTokenRepository.FindByHashAsync(otpHash, cancellationToken);
        if (recoveryToken is null ||
            recoveryToken.Purpose != "ResetPassword" ||
            recoveryToken.UsedAtUtc is not null)
        {
            await LogAuditAsync(null, request.Email ?? "Unknown", "Password_VerifyOtp", "Failed_InvalidOrReusedOtp", corrId, cancellationToken);
            return Result.Failure<string>(Error.Unauthorized("Auth.InvalidOtp", "Mã OTP không hợp lệ hoặc đã được sử dụng."));
        }

        if (recoveryToken.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
        {
            await LogAuditAsync(recoveryToken.UserId, recoveryToken.UserId.ToString(), "Password_VerifyOtp", "Failed_OtpExpired", corrId, cancellationToken);
            return Result.Failure<string>(Error.Unauthorized("Auth.OtpExpired", "Mã OTP đã hết hạn."));
        }

        User? user = await userRepository.GetByIdAsync(recoveryToken.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            await LogAuditAsync(recoveryToken.UserId, recoveryToken.UserId.ToString(), "Password_VerifyOtp", "Failed_UserNotFoundOrInactive", corrId, cancellationToken);
            return Result.Failure<string>(Error.Unauthorized("Auth.InvalidOtp", "Mã OTP không hợp lệ hoặc tài khoản không tồn tại."));
        }

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            string normalizedEmail = NormalizeEmail(request.Email);
            if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                await LogAuditAsync(user.Id, user.Id.ToString(), "Password_VerifyOtp", "Failed_EmailMismatch", corrId, cancellationToken);
                return Result.Failure<string>(Error.Unauthorized("Auth.InvalidOtp", "Mã OTP không khớp với email yêu cầu."));
            }
        }

        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_VerifyOtp", "Success", corrId, cancellationToken);
        return Result.Success(cleanOtp);
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

        if (PasswordPolicy.CheckPersonalInfo(request.NewPassword, user.Email, user.FullName) is Error passwordError)
        {
            return Result.Failure(passwordError);
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

        if (PasswordPolicy.CheckPersonalInfo(request.NewPassword, user.Email, user.FullName) is Error passwordError)
        {
            return Result.Failure(passwordError);
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
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<CurrentUserResponse>(validationError);
        }

        User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
        }

        string? avatarUrl = mediaUrlResolver.ResolvePublicUrl(user.AvatarMediaId);
        return Result.Success(user.ToCurrentUserResponse(avatarUrl));
    }


    public async Task<Result<AdminUserDetailDto>> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? validationError))
        {
            return Result.Failure<AdminUserDetailDto>(validationError);
        }

        User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<AdminUserDetailDto>(AuthErrors.User.NotFound);
        }

        string? avatarUrl = mediaUrlResolver.ResolvePublicUrl(user.AvatarMediaId);
        var detailDto = new AdminUserDetailDto(
            user.Id,
            user.Email,
            user.FullName,
            user.PhoneNumber,
            user.AvatarMediaId,
            avatarUrl,
            user.IsActive,
            user.CreatedAt,
            UserAccessResolver.EffectiveRoles(user),
            user.DateOfBirth,
            user.CitizenId
        );

        return Result.Success(detailDto);
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
            UserAccessResolver.EffectiveRoles(u).ToList()
        )).ToList();

        return Result.Success(new PagedResult<AdminUserListItemDto>(items, request.Page, request.PageSize, totalCount, totalPages));
    }

    public async Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, Guid? adminId = null, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<Guid>(validationError);
        }

        if (PasswordPolicy.CheckPersonalInfo(request.Password, request.Email, request.FullName) is Error passwordError)
        {
            return Result.Failure<Guid>(passwordError);
        }

        if (request.RoleIds is null || request.RoleIds.Count == 0)
        {
            return Result.Failure<Guid>(new Error(
                "User.RolesRequired",
                "Vui lòng chỉ định ít nhất một quyền (Role) cho người dùng mới.",
                ErrorType.Validation));
        }

        List<Guid> distinctRoleIds = request.RoleIds.Distinct().ToList();
        List<Role> rolesToAssign = new();

        foreach (Guid roleId in distinctRoleIds)
        {
            Role? role = await roleRepository.GetByIdAsync(roleId, cancellationToken);
            if (role is null)
            {
                return Result.Failure<Guid>(new Error(
                    "User.InvalidRoles",
                    "Một hoặc nhiều quyền (Role) được cung cấp không tồn tại trong hệ thống.",
                    ErrorType.Validation));
            }

            if (role.Scope != RoleScope.Global)
            {
                return Result.Failure<Guid>(AuthErrors.Role.InvalidScope);
            }

            rolesToAssign.Add(role);
        }

        if (adminId.HasValue)
        {
            IReadOnlyList<string> adminPermissions = await roleRepository.GetUserPermissionCodesAsync(adminId.Value, cancellationToken);
            if (!adminPermissions.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
            {
                return Result.Failure<Guid>(AuthErrors.Role.InsufficientPermission);
            }

            foreach (Role role in rolesToAssign)
            {
                IReadOnlyList<string> rolePermissions = await roleRepository.GetPermissionCodesAsync(role.Id, cancellationToken);
                if (rolePermissions.Except(adminPermissions, StringComparer.Ordinal).Any())
                {
                    return Result.Failure<Guid>(AuthErrors.Role.InsufficientPermission);
                }
            }
        }

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        bool emailExists = await userRepository.EmailExistsAsync(normalizedEmail, cancellationToken);
        if (emailExists)
        {
            return Result.Failure<Guid>(new Error(
                "User.EmailExists",
                "Email này đã được sử dụng.",
                ErrorType.Conflict));
        }

        var newUser = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim(),
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            HasPassword = true
        };

        newUser.PasswordHash = passwordHasher.HashPassword(newUser, request.Password);

        foreach (Guid roleId in distinctRoleIds)
        {
            newUser.UserRoles.Add(new UserRole { RoleId = roleId, UserId = newUser.Id });
        }

        await userRepository.CreateAsync(newUser, cancellationToken);

        return Result.Success(newUser.Id);
    }

    public async Task<Result<IReadOnlyList<RoleDto>>> GetAllRolesAsync(Guid? adminId = null, CancellationToken cancellationToken = default)
    {
        var roles = await roleRepository.GetAllAsync(cancellationToken);
        var globalRoles = roles.Where(r => r.Scope == RoleScope.Global).ToList();

        if (adminId.HasValue)
        {
            var adminPermissions = await roleRepository.GetUserPermissionCodesAsync(adminId.Value, cancellationToken);
            if (!adminPermissions.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
            {
                return Result.Success<IReadOnlyList<RoleDto>>([]);
            }

            var assignableRoles = new List<Role>();
            foreach (var role in globalRoles)
            {
                var rolePermissions = await roleRepository.GetPermissionCodesAsync(role.Id, cancellationToken);
                if (!rolePermissions.Except(adminPermissions, StringComparer.Ordinal).Any())
                {
                    assignableRoles.Add(role);
                }
            }
            globalRoles = assignableRoles;
        }

        var roleDtos = globalRoles.Select(r => new RoleDto(r.Id, r.Name, r.Description)).ToList();
        return Result.Success<IReadOnlyList<RoleDto>>(roleDtos);
    }

    public async Task<Result> UpdateUserAsync(Guid id, UpdateUserRequest request, Guid? adminId = null, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(id, nameof(id), out Error? idError))
        {
            return Result.Failure(idError);
        }

        if (request is null)
        {
            return Result.Failure(Error.Validation("Validation.Required", "Dữ liệu yêu cầu không được để trống."));
        }

        if (request.RemoveAvatar && request.AvatarMediaId.HasValue)
        {
            return Result.Failure(Error.Validation("Validation.AvatarMediaId", "Không thể vừa chọn vừa xóa avatar."));
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        // 1. Kiểm tra Roles nếu được truyền lên
        List<Guid>? distinctRoleIds = null;
        List<Role>? rolesToAssign = null;
        if (request.RoleIds is not null)
        {
            if (request.RoleIds.Count == 0)
            {
                return Result.Failure(new Error(
                    "User.RolesRequired",
                    "Vui lòng chỉ định ít nhất một quyền (Role) cho người dùng.",
                    ErrorType.Validation));
            }

            if (adminId.HasValue && adminId.Value == id)
            {
                return Result.Failure(AuthErrors.Role.CannotSelfAssign);
            }

            distinctRoleIds = request.RoleIds.Distinct().ToList();
            rolesToAssign = new List<Role>();
            foreach (Guid roleId in distinctRoleIds)
            {
                Role? role = await roleRepository.GetByIdAsync(roleId, cancellationToken);
                if (role is null)
                {
                    return Result.Failure(new Error(
                        "User.InvalidRoles",
                        "Một hoặc nhiều quyền (Role) được cung cấp không tồn tại trong hệ thống.",
                        ErrorType.Validation));
                }

                if (role.Scope != RoleScope.Global)
                {
                    return Result.Failure(AuthErrors.Role.InvalidScope);
                }

                rolesToAssign.Add(role);
            }

            if (adminId.HasValue)
            {
                IReadOnlyList<string> adminPermissions = await roleRepository.GetUserPermissionCodesAsync(adminId.Value, cancellationToken);
                if (!adminPermissions.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
                {
                    return Result.Failure(AuthErrors.Role.InsufficientPermission);
                }

                foreach (Role role in rolesToAssign)
                {
                    IReadOnlyList<string> rolePermissions = await roleRepository.GetPermissionCodesAsync(role.Id, cancellationToken);
                    if (rolePermissions.Except(adminPermissions, StringComparer.Ordinal).Any())
                    {
                        return Result.Failure(AuthErrors.Role.InsufficientPermission);
                    }
                }
            }
        }

        // Đã gộp vào phần đầu UpdateUserAsync

        // 2. Lấy thông tin user
        var user = await userRepository.GetByIdWithRolesAsync(id, cancellationToken);
        if (user is null)
        {
            return Result.Failure(new Error("User.NotFound", "Không tìm thấy người dùng.", ErrorType.NotFound));
        }

        // 3. Cập nhật FullName
        string trimmedFullName = request.FullName.Trim();
        if (trimmedFullName.Length == 0)
        {
            return Result.Failure(AuthErrors.Profile.InvalidFullName);
        }
        user.FullName = trimmedFullName;

        // 4. Cập nhật Email (nếu có truyền)
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            string normalizedEmail = NormalizeEmail(request.Email);
            if (!string.Equals(user.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                bool emailExists = await userRepository.EmailExistsAsync(normalizedEmail, cancellationToken);
                if (emailExists)
                {
                    return Result.Failure(AuthErrors.User.EmailAlreadyExists);
                }
                user.Email = normalizedEmail;
            }
        }

        // 5. Cập nhật PhoneNumber
        if (request.PhoneNumber is not null)
        {
            user.PhoneNumber = StringUtility.NullIfBlank(request.PhoneNumber);
        }

        // 6. Cập nhật DateOfBirth
        if (request.DateOfBirth.HasValue)
        {
            if (CustomerAccountRules.IsUnderage(request.DateOfBirth.Value, timeProvider.GetUtcNow()))
            {
                return Result.Failure(AuthErrors.User.Underage);
            }
            user.DateOfBirth = request.DateOfBirth.Value;
        }

        // 7. Cập nhật CitizenId
        if (request.CitizenId is not null)
        {
            string? trimmedCitizenId = StringUtility.NullIfBlank(request.CitizenId);
            if (trimmedCitizenId is not null && !string.Equals(user.CitizenId, trimmedCitizenId, StringComparison.Ordinal))
            {
                if (await userRepository.CitizenIdExistsAsync(trimmedCitizenId, cancellationToken))
                {
                    return Result.Failure(AuthErrors.User.CitizenIdAlreadyExists);
                }
            }
            user.CitizenId = trimmedCitizenId;
        }

        // 8. Cập nhật Avatar
        if (request.RemoveAvatar)
        {
            user.AvatarMediaId = null;
        }
        else if (request.AvatarMediaId.HasValue)
        {
            user.AvatarMediaId = request.AvatarMediaId.Value;
        }

        // 9. Cập nhật IsActive
        if (request.IsActive.HasValue)
        {
            if (user.IsActive && !request.IsActive.Value)
            {
                Result ownerCheck = await EnsureNotLastOwnerAsync(user.Id, cancellationToken);
                if (ownerCheck.IsFailure)
                {
                    return ownerCheck;
                }

                user.IsActive = false;
                await authSessionRepository.RevokeAllSessionsForUserAsync(user.Id, cancellationToken);
            }
            else
            {
                user.IsActive = request.IsActive.Value;
            }
        }

        // 10. Cập nhật Roles (nếu được truyền lên)
        if (distinctRoleIds is not null)
        {
            var currentRoleIds = user.UserRoles.Select(ur => ur.RoleId).ToList();
            var rolesToRemove = user.UserRoles.Where(ur => !distinctRoleIds.Contains(ur.RoleId)).ToList();
            foreach (var role in rolesToRemove)
            {
                user.UserRoles.Remove(role);
            }

            var rolesToAdd = distinctRoleIds.Where(rId => !currentRoleIds.Contains(rId)).ToList();
            foreach (var roleId in rolesToAdd)
            {
                user.UserRoles.Add(new UserRole { RoleId = roleId, UserId = user.Id });
            }

            bool rolesChanged = rolesToRemove.Count > 0 || rolesToAdd.Count > 0;
            if (rolesChanged)
            {
                await authSessionRepository.RevokeAllSessionsForUserAsync(user.Id, cancellationToken);
            }
        }

        // 11. Cập nhật trạng thái hoàn thiện hồ sơ nếu đủ điều kiện
        if (user.ProfileCompletedAtUtc is null
            && !string.IsNullOrWhiteSpace(user.CitizenId)
            && !string.IsNullOrWhiteSpace(user.PhoneNumber)
            && user.DateOfBirth.HasValue)
        {
            user.ProfileCompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        }

        await userRepository.UpdateAsync(user, cancellationToken);

        string corrId = correlationIdProvider.GetCorrelationId();
        await LogAuditAsync(user.Id, user.Id.ToString(), "User_Update", "Success", corrId, cancellationToken);

        return Result.Success();
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

        Result ownerCheck = await EnsureNotLastOwnerAsync(targetUserId, cancellationToken);
        if (ownerCheck.IsFailure)
        {
            await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Failed_LastOwner", corrId, cancellationToken);
            return ownerCheck;
        }

        user.IsActive = false;
        await userRepository.UpdateAsync(user, cancellationToken);
        await authSessionRepository.RevokeAllSessionsForUserAsync(targetUserId, cancellationToken);
        await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Success", corrId, cancellationToken);

        return Result.Success();
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
    }

    private bool IsAdminCreationKeyValid(string? providedKey)
    {
        string configuredKey = adminBootstrapOptions.Value.CreationKey;
        if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrWhiteSpace(providedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configuredKey.Trim()),
            Encoding.UTF8.GetBytes(providedKey.Trim()));
    }

    private static User CreateUserEntity(RegisterAdminRequest request, string email)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            PasswordHash = new PasswordHasher<User>().HashPassword(null!, request.Password),
            HasPassword = true
        };
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

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

    private async Task<Result> EnsureNotLastOwnerAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (organizationMembershipRepository is null)
        {
            return Result.Success();
        }
        IReadOnlyList<OrganizationMembership> memberships = await organizationMembershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);
        List<Guid> ownerOrgIds = memberships
            .Where(m => m.Status == MembershipStatus.Active &&
                m.Role is not null &&
                string.Equals(m.Role.Name, "Owner", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.OrganizationId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (ownerOrgIds.Count == 0)
        {
            return Result.Success();
        }

        IExecutionTransaction? ownerTransaction = null;
        if (organizationRepository is not null)
        {
            ownerTransaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        }
        try
        {
            if (organizationRepository is not null)
            {
                await organizationRepository.LockOrganizationsAsync(ownerOrgIds, cancellationToken);
            }

            foreach (Guid orgId in ownerOrgIds)
            {
                int activeOwners = await organizationMembershipRepository.CountActiveOwnersAsync(orgId, cancellationToken);
                if (activeOwners <= 1)
                {
                    return Result.Failure(AuthErrors.User.LastOwnerOrganization);
                }
            }

            return Result.Success();
        }
        finally
        {
            if (ownerTransaction is not null)
            {
                await ownerTransaction.DisposeAsync();
            }
        }
    }
}
