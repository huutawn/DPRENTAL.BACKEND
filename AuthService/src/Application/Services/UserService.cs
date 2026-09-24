using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Domain.Access;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Dprental.AuthService.Domain.DTOs.User;

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
    IRecoveryTokenRepository recoveryTokenRepository) : IUserService
{
    public async Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (await CustomerAccountRules.ValidateAsync(request, email, userRepository, now, cancellationToken) is Error error)
        {
            return error;
        }

        User user = CustomerAccountRules.Create(request, email, passwordHasher, now.UtcDateTime);
        Role? customerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Customer, cancellationToken);

        try
        {
            if (customerRole is not null)
            {
                await userRepository.CreateWithRoleAsync(user, customerRole.Id, cancellationToken);
            }
            else
            {
                // Role customer chưa được seed (vd DB chưa chạy migration mới nhất) — vẫn cho đăng ký,
                // không chặn user chỉ vì thiếu 1 role không mang permission gì.
                await userRepository.CreateAsync(user, cancellationToken);
            }
        }
        catch (DuplicateEmailException)
        {
            // Request khác đã đăng ký cùng email ngay sau bước kiểm tra ở trên.
            return AuthErrors.User.EmailAlreadyExists;
        }

        return new AccountCreatedResponse(user.Id);
    }

    public async Task<Result<AccountCreatedResponse>> RegisterAdminAsync(
        RegisterAdminRequest request,
        string? creationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAdminCreationKeyValid(creationKey))
        {
            return AuthErrors.AdminBootstrap.InvalidKey;
        }

        string email = NormalizeEmail(request.Email);
        if (await userRepository.EmailExistsAsync(email, cancellationToken))
        {
            return AuthErrors.User.EmailAlreadyExists;
        }

        Role? adminRole = await roleRepository.GetByNameAsync(SystemRoleCode.Admin, cancellationToken);
        if (adminRole is null)
        {
            return AuthErrors.Role.RoleNotFound;
        }

        User user = CreateUserEntity(request, email);

        try
        {
            await userRepository.CreateWithRoleAsync(user, adminRole.Id, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            return AuthErrors.User.EmailAlreadyExists;
        }
        catch (KeyNotFoundException)
        {
            return AuthErrors.Role.RoleNotFound;
        }

        return new AccountCreatedResponse(user.Id);
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

    public async Task<Guid?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        User? user = await userRepository.FindByEmailAsync(email, cancellationToken);
        string corrId = correlationIdProvider.GetCorrelationId();

        // 1. Kiểm tra tài khoản tồn tại
        if (user is null)
        {
            await LogAuditAsync(null, email, "Login", "Failed_UserNotFound", corrId, cancellationToken);
            return null;
        }

        // 2. Kiểm tra trạng thái kích hoạt
        if (!user.IsActive)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_AccountDisabled", corrId, cancellationToken);
            return null;
        }

        // 3. Kiểm tra trạng thái khóa tài khoản (Lockout)
        if (user.LockoutEnd is not null && user.LockoutEnd > timeProvider.GetUtcNow())
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_LockedOut", corrId, cancellationToken);
            return null;
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
            return null;
        }

        // 5. Cập nhật lại Hash nếu thuật toán mã hóa thay đổi (SuccessRehashNeeded)
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        }

        // 6. Đăng nhập thành công -> Reset bộ đếm lỗi và thời gian khóa
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        await userRepository.UpdateAsync(user, cancellationToken);
        await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Success", corrId, cancellationToken);

        return user.Id;
    }



    public async Task<Result> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        // Tạo mã token raw (để gửi qua email cho user)
        string rawToken = RefreshTokenUtility.Create();

        // Tạo entity RecoveryToken lưu vào database (chỉ lưu mã băm Hash)
        RecoveryToken recoveryToken = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = RefreshTokenUtility.ComputeHash(rawToken),
            Purpose = "ResetPassword",
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            ExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(30) // TTL 30 phút
        };

        await recoveryTokenRepository.AddAsync(recoveryToken, cancellationToken);

        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Success", corrId, cancellationToken);

        // Lưu ý: Ở ứng dụng thực tế, bạn sẽ gọi một EmailService ở đây để gửi biến `rawToken` cho người dùng.

        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string corrId = correlationIdProvider.GetCorrelationId();

        // 1. Tìm User
        User? user = await userRepository.FindByEmailAsync(NormalizeEmail(request.Email), cancellationToken);
        if (user is null)
        {
            // Ghi log chung chung để chống dò quét email (Email Enumeration)
            await LogAuditAsync(null, request.Email, "Password_Reset", "Failed_UserNotFound", corrId, cancellationToken);
            return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã không hợp lệ hoặc tài khoản không tồn tại."));
        }

        // 2. Băm token từ request để tìm kiếm trong DB (Tuyệt đối không tìm bằng token raw)
        string incomingTokenHash = RefreshTokenUtility.ComputeHash(request.Token); // Gọi hàm băm SHA256 tương tự như RefreshToken

        // 3. Tìm token trong bảng RecoveryTokens
        RecoveryToken? recoveryToken = await recoveryTokenRepository.FindByHashAsync(incomingTokenHash, cancellationToken);

        // 4. Validate Token (Có tồn tại? Của đúng User? Đúng mục đích? Đã bị dùng chưa?)
        if (recoveryToken is null ||
            recoveryToken.UserId != user.Id ||
            recoveryToken.Purpose != "ResetPassword" ||
            recoveryToken.UsedAtUtc is not null)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Failed_InvalidOrReusedToken", corrId, cancellationToken);
            return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã khôi phục không hợp lệ hoặc đã được sử dụng."));
        }

        // 5. Kiểm tra thời hạn (TTL)
        if (recoveryToken.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Failed_TokenExpired", corrId, cancellationToken);
            return Result.Failure(Error.Unauthorized("Auth.TokenExpired", "Mã khôi phục đã hết hạn."));
        }

        // 6. Đổi mật khẩu & Reset Abuse Protection (Mở khóa tài khoản)
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.HasPassword = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await userRepository.UpdateAsync(user, cancellationToken);

        // 7. Đánh dấu Token đã sử dụng (One-time use)
        recoveryToken.UsedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await recoveryTokenRepository.UpdateAsync(recoveryToken, cancellationToken);

        // 8. Kích hoạt Kill Switch: Đăng xuất tất cả các thiết bị sau khi đổi mật khẩu
        await authSessionRepository.RevokeAllSessionsForUserAsync(user.Id, cancellationToken);

        // 9. Ghi Audit Log thành công
        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Success", corrId, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.HasPassword = true;
        await userRepository.UpdateAsync(user, cancellationToken);

        await authSessionRepository.RevokeAllSessionsForUserAsync(user.Id, cancellationToken);
        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Change", "Success", corrId, cancellationToken);
        return Result.Success();
    }

    private async Task LogAuditAsync(Guid? actorId, string target, string action, string result, string corrId, CancellationToken cancellationToken)
    {
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = target,
            Action = action,
            Result = result,
            CorrelationId = corrId,
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.User.NotFound;
        }

        return user.ToCurrentUserResponse();
    }

    public async Task<Result> UpdateUserProfileAsync(Guid userId, UpdateUserProfileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null) return Result.Failure(AuthErrors.User.NotFound);

        user.FullName = request.FullName.Trim();
        user.PhoneNumber = request.PhoneNumber?.Trim();
        user.AvatarUrl = request.AvatarUrl?.Trim();

        await userRepository.UpdateAsync(user, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<AdminUserDetailDto>> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        if (user is null) return Result.Failure<AdminUserDetailDto>(AuthErrors.User.NotFound);

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

        return detailDto;
    }

    public async Task<Result<PagedResult<AdminUserListItemDto>>> GetUsersPagedAsync(PageRequest pageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageRequest);

        Result validation = pageRequest.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<AdminUserListItemDto>>(validation.Error);
        }

        int skip = (pageRequest.Page - 1) * pageRequest.PageSize;

        var (users, totalCount) = await userRepository.GetUsersPagedAsync(skip, pageRequest.PageSize, cancellationToken);

        int totalPages = (int)Math.Ceiling(totalCount / (double)pageRequest.PageSize);

        var items = users.Select(u => new AdminUserListItemDto(
            u.Id,
            u.Email,
            u.FullName,
            u.IsActive,
            u.CreatedAt)).ToList();

        return new PagedResult<AdminUserListItemDto>(items, pageRequest.Page, pageRequest.PageSize, totalCount, totalPages);
    }

    public async Task<Result> SuspendUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdAsync(targetUserId, cancellationToken);
        string corrId = correlationIdProvider.GetCorrelationId();

        if (user is null)
        {
            await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Failed_UserNotFound", corrId, cancellationToken);
            return Result.Failure(AuthErrors.User.NotFound);
        }

        if (!user.IsActive) return Result.Success();

        user.IsActive = false;
        await userRepository.UpdateAsync(user, cancellationToken);
        await authSessionRepository.RevokeAllSessionsForUserAsync(targetUserId, cancellationToken);
        await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Suspend", "Success", corrId, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> ActivateUserAsync(Guid targetUserId, Guid adminId, CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdAsync(targetUserId, cancellationToken);
        string corrId = correlationIdProvider.GetCorrelationId();

        if (user is null)
        {
            await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Activate", "Failed_UserNotFound", corrId, cancellationToken);
            return Result.Failure(AuthErrors.User.NotFound); 
        }

        if (user.IsActive) return Result.Success();

        user.IsActive = true;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await userRepository.UpdateAsync(user, cancellationToken);
        await LogAuditAsync(adminId, targetUserId.ToString(), "Account_Activate", "Success", corrId, cancellationToken);

        return Result.Success();
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
