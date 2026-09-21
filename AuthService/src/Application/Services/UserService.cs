using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Utils;
using Microsoft.AspNetCore.Identity;
using System.Diagnostics.CodeAnalysis;

namespace Dprental.AuthService.Application.Services;

public sealed class UserService(
    IUserRepository userRepository,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IAuditLogRepository auditLogRepository,
    IAuthSessionRepository authSessionRepository) : IUserService
{
    public async Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        if (await userRepository.EmailExistsAsync(email, cancellationToken))
        {
            return UserErrors.EmailAlreadyExists;
        }

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            IsActive = true,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        try
        {
            await userRepository.CreateAsync(user, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            // Request khác đã đăng ký cùng email ngay sau bước kiểm tra ở trên.
            return UserErrors.EmailAlreadyExists;
        }

        return new AccountCreatedResponse(user.Id);
    }

    public async Task<Guid?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        User? user = await userRepository.FindByEmailAsync(email, cancellationToken);
        string corrId = correlationIdProvider.GetCorrelationId();

        if (user is null)
        {
            await LogAuditAsync(null, email, "Login", "Failed_UserNotFound", corrId, cancellationToken);
            return null;
        }

        if (!user.IsActive)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_AccountDisabled", corrId, cancellationToken);
            return null;
        }

        if (user.LockoutEnd is not null && user.LockoutEnd > timeProvider.GetUtcNow())
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_LockedOut", corrId, cancellationToken);
            return null;
        }

        PasswordVerificationResult verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification is not PasswordVerificationResult.Success and not PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= 5)
            {
                user.LockoutEnd = timeProvider.GetUtcNow().AddMinutes(15);
            }

            await userRepository.UpdateAsync(user, cancellationToken);
            await LogAuditAsync(user.Id, user.Id.ToString(), "Login", "Failed_InvalidCredentials", corrId, cancellationToken);
            return null;
        }

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

        // Tạo token mới, lưu hash
        string rawToken = RefreshTokenUtility.Create();
        user.PasswordResetToken = RefreshTokenUtility.ComputeHash(rawToken);
        user.ResetTokenExpiry = timeProvider.GetUtcNow().AddMinutes(30);
        await userRepository.UpdateAsync(user, cancellationToken);

        await LogAuditAsync(user.Id, user.Id.ToString(), "Password_ResetRequest", "Success", corrId, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? user = await userRepository.FindByEmailAsync(NormalizeEmail(request.Email), cancellationToken);
        string corrId = correlationIdProvider.GetCorrelationId();

        if (user is null || string.IsNullOrWhiteSpace(user.PasswordResetToken) || user.PasswordResetToken != request.Token)
        {
            await LogAuditAsync(user?.Id, user?.Id.ToString() ?? request.Email, "Password_Reset", "Failed_InvalidToken", corrId, cancellationToken);
            return Result.Failure(Error.Unauthorized("Auth.ResetFailed", "Mã không hợp lệ."));
        }

        if (user.ResetTokenExpiry is null || user.ResetTokenExpiry <= timeProvider.GetUtcNow())
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Password_Reset", "Failed_TokenExpired", corrId, cancellationToken);
            return Result.Failure(Error.Unauthorized("Auth.TokenExpired", "Mã đã hết hạn."));
        }

        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.PasswordResetToken = null;
        user.ResetTokenExpiry = null;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await userRepository.UpdateAsync(user, cancellationToken);

        await authSessionRepository.RevokeAllSessionsForUserAsync(user.Id, cancellationToken);
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

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
