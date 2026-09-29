using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;
using Dprental.AuthService.Common.Utils;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối các thao tác tự quản lý tài khoản: hoàn thiện hồ sơ, cập nhật hồ sơ, bảo mật và liên kết.</summary>
public sealed class UserAccountService(
    IUserRepository userRepository,
    IExternalLoginRepository externalLoginRepository,
    IAuthSessionRepository authSessionRepository,
    IOrganizationMembershipRepository organizationMembershipRepository,
    IOrganizationRepository organizationRepository,
    IAuditLogRepository auditLogRepository,
    ICorrelationIdProvider correlationIdProvider,
    TimeProvider timeProvider,
    IMediaServiceClient mediaServiceClient,
    IMediaUrlResolver mediaUrlResolver,
    IMediaOutboxService mediaOutboxService,
    ILogger<UserAccountService> logger) : IUserAccountService
{
    public async Task<Result<CurrentUserResponse>> CompleteProfileAsync(
        Guid userId,
        CompleteProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure<CurrentUserResponse>(idError);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<CurrentUserResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "CompleteProfile",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
                }

                if (!user.IsActive)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.Auth.InactiveAccount);
                }

                if (user.IsProfileCompleted)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.Profile.AlreadyCompleted);
                }

                DateOnly dob = request.DateOfBirth!.Value;
                if (CustomerAccountRules.IsUnderage(dob, timeProvider.GetUtcNow()))
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.Underage);
                }

                string citizenId = request.CitizenId.Trim();
                if (await userRepository.CitizenIdExistsAsync(citizenId, cancellationToken))
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.CitizenIdAlreadyExists);
                }

                user.CitizenId = citizenId;
                user.PhoneNumber = request.PhoneNumber.Trim();
                user.DateOfBirth = dob;
                user.ProfileCompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

                if (StringUtility.NullIfBlank(request.FullName) is string fullName)
                {
                    user.FullName = fullName;
                }

                if (request.AvatarMediaId.HasValue)
                {
                    var verifyResult = await mediaServiceClient.VerifyMediaAsync(request.AvatarMediaId.Value, cancellationToken);
                    if (verifyResult.IsFailure)
                    {
                        return Result.Failure<CurrentUserResponse>(verifyResult.Error);
                    }
                    var validation = MediaVerificationRules.ValidateImage(verifyResult.Value, userId);
                    if (validation.IsFailure)
                    {
                        return Result.Failure<CurrentUserResponse>(validation.Error);
                    }
                    user.AvatarMediaId = request.AvatarMediaId.Value;
                    await mediaOutboxService.QueuePublicVisibilityAsync(request.AvatarMediaId.Value, cancellationToken);
                }

                if (!await userRepository.UpdateAsync(user, cancellationToken))
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
                }
                await LogAuditAsync(user.Id, user.Id.ToString(), "Profile_Complete", "Success", cancellationToken);

                User? reloaded = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
                return Result.Success((reloaded ?? user).ToCurrentUserResponse(mediaUrlResolver.ResolvePublicUrl((reloaded ?? user).AvatarMediaId)));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public Task<Result<CurrentUserResponse>> UpdateProfileAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateProfileCoreAsync(userId, request, clearPhoneWhenNull: false, cancellationToken: cancellationToken);

    private async Task<Result<CurrentUserResponse>> UpdateProfileCoreAsync(
        Guid userId,
        UpdateProfileRequest request,
        bool clearPhoneWhenNull,
        CancellationToken cancellationToken)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure<CurrentUserResponse>(idError);
        }

        if (request is null)
        {
            return Result.Failure<CurrentUserResponse>(AuthErrors.Profile.EmptyPatch);
        }

        if (request.RemoveAvatar && request.AvatarMediaId.HasValue)
        {
            return Result.Failure<CurrentUserResponse>(Error.Validation("Validation.AvatarMediaId", "Không thể vừa chọn vừa xóa avatar."));
        }

        string? trimmedFullName = request.FullName?.Trim();
        if (trimmedFullName is not null && trimmedFullName.Length == 0)
        {
            return Result.Failure<CurrentUserResponse>(AuthErrors.Profile.InvalidFullName);
        }

        bool hasAnyField = trimmedFullName is not null
            || request.PhoneNumber is not null
            || request.DateOfBirth.HasValue
            || request.AvatarMediaId.HasValue
            || request.RemoveAvatar;

        if (!hasAnyField)
        {
            return Result.Failure<CurrentUserResponse>(AuthErrors.Profile.EmptyPatch);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<CurrentUserResponse>(validationError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "UpdateProfile",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
                }

                if (!user.IsActive)
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.Auth.InactiveAccount);
                }


                if (trimmedFullName is not null)
                {
                    user.FullName = trimmedFullName;
                }

                if (request.PhoneNumber is not null || clearPhoneWhenNull)
                {
                    user.PhoneNumber = request.PhoneNumber?.Trim();
                }

                if (request.DateOfBirth.HasValue)
                {
                    if (CustomerAccountRules.IsUnderage(request.DateOfBirth.Value, timeProvider.GetUtcNow()))
                    {
                        return Result.Failure<CurrentUserResponse>(AuthErrors.User.Underage);
                    }
                    user.DateOfBirth = request.DateOfBirth.Value;
                }

                if (request.RemoveAvatar)
                {
                    user.AvatarMediaId = null;
                }
                else if (request.AvatarMediaId.HasValue)
                {
                    // MediaId đã gắn sẵn được phép gửi lại khi cập nhật hồ sơ, kể cả file nhập từ dữ liệu cũ.
                    if (user.AvatarMediaId != request.AvatarMediaId.Value)
                    {
                        var verifyResult = await mediaServiceClient.VerifyMediaAsync(request.AvatarMediaId.Value, cancellationToken);
                        if (verifyResult.IsFailure)
                        {
                            return Result.Failure<CurrentUserResponse>(verifyResult.Error);
                        }
                        var validation = MediaVerificationRules.ValidateImage(verifyResult.Value, userId);
                        if (validation.IsFailure)
                        {
                            return Result.Failure<CurrentUserResponse>(validation.Error);
                        }
                        user.AvatarMediaId = request.AvatarMediaId.Value;
                        await mediaOutboxService.QueuePublicVisibilityAsync(request.AvatarMediaId.Value, cancellationToken);
                    }
                }

                if (!await userRepository.UpdateAsync(user, cancellationToken))
                {
                    return Result.Failure<CurrentUserResponse>(AuthErrors.User.NotFound);
                }
                await LogAuditAsync(user.Id, user.Id.ToString(), "Profile_Update", "Success", cancellationToken);

                User? reloaded = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
                return Result.Success((reloaded ?? user).ToCurrentUserResponse(mediaUrlResolver.ResolvePublicUrl((reloaded ?? user).AvatarMediaId)));
            },
            logger,
            correlationIdProvider,
            cancellationToken: cancellationToken);
    }

    public async Task<Result<CurrentUserResponse>> UpdateUserProfileAsync(
        Guid userId,
        Dprental.AuthService.Domain.DTOs.User.UpdateUserProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<CurrentUserResponse>(validationError);
        }
        return await UpdateProfileCoreAsync(
            userId,
            new UpdateProfileRequest(
                request.FullName,
                request.PhoneNumber,
                null,
                request.AvatarMediaId,
                request.RemoveAvatar),
            clearPhoneWhenNull: true,
            cancellationToken: cancellationToken);
    }

    public async Task<Result<IReadOnlyList<ExternalLoginResponse>>> GetExternalLoginsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure<IReadOnlyList<ExternalLoginResponse>>(idError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetExternalLogins",
            async () =>
            {
                User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
                if (user is null)
                {
                    return Result.Failure<IReadOnlyList<ExternalLoginResponse>>(AuthErrors.User.NotFound);
                }

                if (!user.IsActive)
                {
                    return Result.Failure<IReadOnlyList<ExternalLoginResponse>>(AuthErrors.Auth.InactiveAccount);
                }

                IReadOnlyList<ExternalLogin> logins = await externalLoginRepository.GetByUserAsync(userId, cancellationToken);
                var responses = logins
                    .Select(login => new ExternalLoginResponse(login.Provider, login.Email, login.CreatedAtUtc))
                    .ToList();

                return Result.Success<IReadOnlyList<ExternalLoginResponse>>(responses);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> UnlinkExternalLoginAsync(
        Guid userId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure(idError);
        }

        if (string.IsNullOrWhiteSpace(provider) || !string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.ExternalLogin.NotLinked);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "UnlinkExternalLogin",
            async () =>
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

                IReadOnlyList<ExternalLogin> logins = await externalLoginRepository.GetByUserAsync(userId, cancellationToken);
                ExternalLogin? targetLogin = logins.FirstOrDefault(l => string.Equals(l.Provider, provider, StringComparison.OrdinalIgnoreCase));
                if (targetLogin is null)
                {
                    return Result.Failure(AuthErrors.ExternalLogin.NotLinked);
                }

                bool hasOtherCredentials = user.HasPassword || logins.Count > 1;
                if (!hasOtherCredentials)
                {
                    return Result.Failure(AuthErrors.ExternalLogin.CannotUnlinkLastCredential);
                }

                await externalLoginRepository.DeleteAsync(userId, provider, cancellationToken);
                await LogAuditAsync(user.Id, user.Id.ToString(), "ExternalLogin_Unlink", "Success", cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> DeactivateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure(idError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "DeactivateAccount",
            async () =>
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

                // Khóa các organization liên quan theo thứ tự cố định trước khi Service kiểm tra Owner cuối cùng
                List<Guid> ownerOrgIds = memberships
                    .Where(m => m.Status == MembershipStatus.Active &&
                        m.Role is not null &&
                        string.Equals(m.Role.Name, "Owner", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.OrganizationId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
                IExecutionTransaction? ownerTransaction = null;
                if (ownerOrgIds.Count > 0)
                {
                    ownerTransaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
                }

                try
                {
                    if (ownerOrgIds.Count > 0)
                    {
                        await organizationRepository.LockOrganizationsAsync(ownerOrgIds, cancellationToken);

                        foreach (Guid orgId in ownerOrgIds)
                        {
                            int activeOwners = await organizationMembershipRepository.CountActiveOwnersAsync(orgId, cancellationToken);
                            if (activeOwners <= 1)
                            {
                                return Result.Failure(AuthErrors.User.LastOwnerOrganization);
                            }
                        }
                    }

                    user.IsActive = false;
                    await userRepository.UpdateAsync(user, cancellationToken);
                    await authSessionRepository.RevokeAllSessionsForUserAsync(userId, cancellationToken);
                    if (ownerTransaction is not null)
                    {
                        await ownerTransaction.CommitAsync(cancellationToken);
                    }
                    await LogAuditAsync(user.Id, user.Id.ToString(), "Account_Deactivate", "Success", cancellationToken);
                    return Result.Success();
                }
                finally
                {
                    if (ownerTransaction is not null)
                    {
                        await ownerTransaction.DisposeAsync();
                    }
                }
            },
            logger,
            correlationIdProvider,
            cancellationToken);
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
