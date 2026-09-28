using System.Security.Cryptography;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Đăng nhập bằng nhà cung cấp bên ngoài: tra theo subject trước, chỉ liên kết theo email khi nhà cung cấp
/// đã xác minh email đó, và chỉ tạo user mới khi chưa có ai dùng email này. Liên kết vào tài khoản đang có
/// mật khẩu thì vô hiệu mật khẩu đó, vì email của tài khoản đăng ký bằng mật khẩu chưa được xác minh.
/// </summary>
public sealed class ExternalLoginService(
    IExternalLoginRepository externalLoginRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    IPasswordHasher<User> passwordHasher,
    IAuditLogRepository auditLogRepository,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<ExternalLoginService> logger) : IExternalLoginService
{
    private const int FullNameMaxLength = 100;

    public async Task<Result<Guid>> SignInAsync(ExternalIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(identity.Provider) || string.IsNullOrWhiteSpace(identity.Subject))
        {
            return Result.Failure<Guid>(Error.Validation("Validation.Identity", "Thông tin danh tính bên ngoài không hợp lệ."));
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ExternalLoginSignIn",
            async () =>
            {
                // Đã liên kết trước đó: tra theo subject, không theo email, nên vẫn đúng khi user đổi email ở nhà cung cấp.
                ExternalLogin? existing = await externalLoginRepository.FindAsync(identity.Provider, identity.Subject, cancellationToken);
                if (existing is not null)
                {
                    return existing.User.IsActive
                        ? Result.Success(existing.UserId)
                        : Result.Failure<Guid>(AuthErrors.ExternalLogin.AccountDisabled);
                }

                if (string.IsNullOrWhiteSpace(identity.Email))
                {
                    return Result.Failure<Guid>(AuthErrors.ExternalLogin.MissingEmail);
                }

                // Email chưa xác minh không đủ tin cậy để gắn vào tài khoản có sẵn: kẻ xấu có thể đăng ký email của người khác.
                if (!identity.EmailVerified)
                {
                    return Result.Failure<Guid>(AuthErrors.ExternalLogin.EmailNotVerified);
                }

                string email = UserService.NormalizeEmail(identity.Email);
                DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;

                User? user = await userRepository.FindByEmailAsync(email, cancellationToken);
                if (user is not null)
                {
                    return await LinkToExistingUserAsync(user, identity, email, utcNow, cancellationToken);
                }

                return await CreateUserAsync(identity, email, utcNow, cancellationToken);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    private async Task<Result<Guid>> LinkToExistingUserAsync(
        User user,
        ExternalIdentity identity,
        string email,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (!user.IsActive)
        {
            return Result.Failure<Guid>(AuthErrors.ExternalLogin.AccountDisabled);
        }

        try
        {
            ExternalLogin login = NewLogin(user.Id, identity, email, utcNow);
            if (user.HasPassword)
            {
                // /register không xác minh email nên không biết ai đã đặt mật khẩu này; nhà cung cấp thì đã xác minh
                // người đang đăng nhập là chủ email. Vô hiệu mật khẩu cũ và mọi phiên để người đặt nó không vào được nữa.
                await externalLoginRepository.AddAndInvalidatePasswordAsync(login, CreateUnusablePasswordHash(user), utcNow, cancellationToken);
                await LogAuditAsync(user.Id, identity.Provider, "ExternalLogin_LinkInvalidatedPassword", cancellationToken);
            }
            else
            {
                await externalLoginRepository.AddAsync(login, cancellationToken);
            }

            return Result.Success(user.Id);
        }
        catch (StorageConstraintViolationException)
        {
            // Hoặc request khác vừa liên kết cùng tài khoản (thì dùng lại), hoặc user này đã gắn một tài khoản khác.
            ExternalLogin? raced = await externalLoginRepository.FindAsync(identity.Provider, identity.Subject, cancellationToken);
            return raced is not null && raced.UserId == user.Id
                ? Result.Success(user.Id)
                : Result.Failure<Guid>(AuthErrors.ExternalLogin.AlreadyLinked);
        }
    }

    private async Task<Result<Guid>> CreateUserAsync(
        ExternalIdentity identity,
        string email,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = BuildFullName(identity.FullName, email),
            CreatedAt = utcNow,
            HasPassword = false,
            IsActive = true,
        };

        user.PasswordHash = CreateUnusablePasswordHash(user);

        Role? customerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Customer, cancellationToken);
        user.ExternalLogins.Add(NewLogin(user.Id, identity, email, utcNow));

        try
        {
            if (customerRole is not null)
            {
                await userRepository.CreateWithRoleAsync(user, customerRole.Id, cancellationToken);
            }
            else
            {
                await userRepository.CreateAsync(user, cancellationToken);
            }

            return Result.Success(user.Id);
        }
        catch (StorageConstraintViolationException)
        {
            // Request khác vừa tạo xong cùng tài khoản: đọc lại thay vì báo lỗi cho người dùng.
            ExternalLogin? raced = await externalLoginRepository.FindAsync(identity.Provider, identity.Subject, cancellationToken);
            if (raced is not null)
            {
                return Result.Success(raced.UserId);
            }

            return Result.Failure<Guid>(AuthErrors.User.EmailAlreadyExists);
        }
    }

    /// <summary>Hash của mật khẩu ngẫu nhiên không ai biết: tài khoản chỉ đăng nhập được qua nhà cung cấp bên ngoài.</summary>
    private string CreateUnusablePasswordHash(User user) =>
        passwordHasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private Task LogAuditAsync(Guid userId, string provider, string action, CancellationToken cancellationToken) =>
        auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Target = userId.ToString(),
            Action = action,
            Result = $"Success: {provider}",
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);

    private static ExternalLogin NewLogin(Guid userId, ExternalIdentity identity, string email, DateTime utcNow) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = identity.Provider,
            ProviderSubject = identity.Subject,
            Email = email,
            CreatedAtUtc = utcNow
        };

    private static string BuildFullName(string? fullName, string email)
    {
        string name = string.IsNullOrWhiteSpace(fullName) ? email : fullName.Trim();
        return name.Length <= FullNameMaxLength ? name : name[..FullNameMaxLength];
    }
}
