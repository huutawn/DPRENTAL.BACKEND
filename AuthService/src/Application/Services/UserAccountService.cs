using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối các thao tác tự quản lý tài khoản: hoàn thiện hồ sơ, cập nhật hồ sơ, bảo mật và liên kết.</summary>
public sealed class UserAccountService(
    IUserRepository userRepository,
    IExternalLoginRepository externalLoginRepository,
    IAuthSessionRepository authSessionRepository,
    IOrganizationMembershipRepository organizationMembershipRepository,
    IAuditLogRepository auditLogRepository,
    ICorrelationIdProvider correlationIdProvider,
    TimeProvider timeProvider) : IUserAccountService
{
    public async Task<Result<CurrentUserResponse>> CompleteProfileAsync(
        Guid userId,
        CompleteProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.User.NotFound;
        }

        if (!user.IsActive)
        {
            return AuthErrors.Auth.InactiveAccount;
        }

        if (user.IsProfileCompleted)
        {
            return AuthErrors.Profile.AlreadyCompleted;
        }

        DateOnly dob = request.DateOfBirth!.Value;
        if (CustomerAccountRules.IsUnderage(dob, timeProvider.GetUtcNow()))
        {
            return AuthErrors.User.Underage;
        }

        string citizenId = request.CitizenId.Trim();
        if (await userRepository.CitizenIdExistsAsync(citizenId, cancellationToken))
        {
            return AuthErrors.User.CitizenIdAlreadyExists;
        }

        user.CitizenId = citizenId;
        user.PhoneNumber = request.PhoneNumber.Trim();
        user.DateOfBirth = dob;
        user.ProfileCompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (CustomerAccountRules.NullIfBlank(request.FullName) is string fullName)
        {
            user.FullName = fullName;
        }

        if (CustomerAccountRules.NullIfBlank(request.AvatarUrl) is string avatarUrl)
        {
            user.AvatarUrl = avatarUrl;
        }

        try
        {
            await userRepository.UpdateAsync(user, cancellationToken);
        }
        catch (DuplicateCitizenIdException)
        {
            return AuthErrors.User.CitizenIdAlreadyExists;
        }

        await LogAuditAsync(user.Id, user.Id.ToString(), "Profile_Complete", "Success", cancellationToken);

        User? reloaded = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        return (reloaded ?? user).ToCurrentUserResponse();
    }

    public async Task<Result<CurrentUserResponse>> UpdateProfileAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.User.NotFound;
        }

        if (!user.IsActive)
        {
            return AuthErrors.Auth.InactiveAccount;
        }

        if (request.FullName is null && request.PhoneNumber is null && request.DateOfBirth is null && request.AvatarUrl is null)
        {
            return AuthErrors.Profile.EmptyPatch;
        }

        if (request.FullName is not null)
        {
            string? trimmedName = CustomerAccountRules.NullIfBlank(request.FullName);
            if (trimmedName is null)
            {
                return AuthErrors.Profile.InvalidFullName;
            }
            user.FullName = trimmedName;
        }

        if (request.PhoneNumber is not null)
        {
            string? trimmedPhone = CustomerAccountRules.NullIfBlank(request.PhoneNumber);
            if (trimmedPhone is null)
            {
                return AuthErrors.Profile.EmptyPatch;
            }
            user.PhoneNumber = trimmedPhone;
        }

        if (request.DateOfBirth is not null)
        {
            if (CustomerAccountRules.IsUnderage(request.DateOfBirth.Value, timeProvider.GetUtcNow()))
            {
                return AuthErrors.User.Underage;
            }
            user.DateOfBirth = request.DateOfBirth.Value;
        }

        if (request.AvatarUrl is not null)
        {
            // AvatarUrl: null = không đổi, "" = xoá về null (field duy nhất xoá được qua self-service)
            user.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        }

        await userRepository.UpdateAsync(user, cancellationToken);
        await LogAuditAsync(user.Id, user.Id.ToString(), "Profile_Update", "Success", cancellationToken);

        User? reloaded = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        return (reloaded ?? user).ToCurrentUserResponse();
    }

    public async Task<Result<IReadOnlyList<ExternalLoginResponse>>> GetExternalLoginsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.User.NotFound;
        }

        if (!user.IsActive)
        {
            return AuthErrors.Auth.InactiveAccount;
        }

        IReadOnlyList<ExternalLogin> logins = await externalLoginRepository.GetByUserAsync(userId, cancellationToken);
        IReadOnlyList<ExternalLoginResponse> responses = logins
            .Select(l => new ExternalLoginResponse(l.Provider, l.Email, l.CreatedAtUtc))
            .ToList();

        return Result.Success(responses);
    }

    public async Task<Result> UnlinkExternalLoginAsync(
        Guid userId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || !string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.ExternalLogin.NotLinked);
        }

        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AuthErrors.User.NotFound);
        }

        if (!user.IsActive)
        {
            return Result.Failure(AuthErrors.Auth.InactiveAccount);
        }

        IReadOnlyList<ExternalLogin> logins = await externalLoginRepository.GetByUserAsync(userId, cancellationToken);
        ExternalLogin? target = logins.FirstOrDefault(l => string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return Result.Failure(AuthErrors.ExternalLogin.NotLinked);
        }

        if (!user.HasPassword && logins.Count <= 1)
        {
            return Result.Failure(AuthErrors.ExternalLogin.CannotUnlinkLastCredential);
        }

        bool deleted = await externalLoginRepository.DeleteAsync(userId, target.Provider, cancellationToken);
        if (!deleted)
        {
            return Result.Failure(AuthErrors.ExternalLogin.NotLinked);
        }

        await LogAuditAsync(user.Id, user.Id.ToString(), "ExternalLogin_Unlink", "Success", cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeactivateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(AuthErrors.User.NotFound);
        }

        if (!user.IsActive)
        {
            return Result.Failure(AuthErrors.Auth.InactiveAccount);
        }

        IReadOnlyList<OrganizationMembership> memberships = await organizationMembershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

        foreach (OrganizationMembership membership in memberships)
        {
            if (membership.Status == MembershipStatus.Active &&
                membership.Role is not null &&
                string.Equals(membership.Role.Name, "Owner", StringComparison.OrdinalIgnoreCase))
            {
                int activeOwners = await organizationMembershipRepository.CountActiveOwnersAsync(membership.OrganizationId, cancellationToken);
                if (activeOwners <= 1)
                {
                    return Result.Failure(AuthErrors.User.LastOwnerOrganization);
                }
            }
        }

        user.IsActive = false;
        await userRepository.UpdateAsync(user, cancellationToken);
        await authSessionRepository.RevokeAllSessionsForUserAsync(userId, cancellationToken);
        await LogAuditAsync(user.Id, user.Id.ToString(), "Account_Deactivate", "Success", cancellationToken);
        return Result.Success();
    }

    private Task LogAuditAsync(Guid? actorId, string target, string action, string result, CancellationToken cancellationToken) =>
        auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            Target = target,
            Action = action,
            Result = result,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
}
