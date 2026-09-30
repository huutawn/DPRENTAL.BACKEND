using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.User;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Repositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Utils;

namespace Dprental.AuthService.Application.Services;

/// <summary>Điều phối các thao tác tự quản lý tài khoản: hoàn thiện hồ sơ, cập nhật hồ sơ, bảo mật và liên kết.</summary>
public sealed class UserAccountService(
    IUserRepository userRepository,
    IExternalLoginRepository externalLoginRepository,
    IFirebaseIdentityService firebaseIdentityService,
    IAuthSessionRepository authSessionRepository,
    IOrganizationMembershipRepository organizationMembershipRepository,
    IOrganizationRepository organizationRepository,
    IAuditLogRepository auditLogRepository,
    ICorrelationIdProvider correlationIdProvider,
    TimeProvider timeProvider,
    IMediaServiceClient mediaServiceClient,
    IMediaUrlResolver mediaUrlResolver,
    IMediaOutboxService mediaOutboxService) : IUserAccountService
{
    private const string GoogleProvider = "Google";
    private const string FirebaseGoogleSignInProvider = "google.com";

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
        string? trimmedCitizenId = StringUtility.NullIfBlank(request.CitizenId);

        bool hasAnyField = trimmedFullName is not null
            || request.PhoneNumber is not null
            || request.DateOfBirth.HasValue
            || trimmedCitizenId is not null
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

        if (trimmedCitizenId is not null && !string.Equals(user.CitizenId, trimmedCitizenId, StringComparison.Ordinal))
        {
            if (await userRepository.CitizenIdExistsAsync(trimmedCitizenId, cancellationToken))
            {
                return Result.Failure<CurrentUserResponse>(AuthErrors.User.CitizenIdAlreadyExists);
            }
            user.CitizenId = trimmedCitizenId;
        }

        if (user.ProfileCompletedAtUtc is null
            && !string.IsNullOrWhiteSpace(user.CitizenId)
            && !string.IsNullOrWhiteSpace(user.PhoneNumber)
            && user.DateOfBirth.HasValue)
        {
            user.ProfileCompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
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
                request.DateOfBirth,
                request.AvatarMediaId,
                request.RemoveAvatar,
                request.CitizenId),
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
    }

    public async Task<Result<CitizenIdResponse>> GetCitizenIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure<CitizenIdResponse>(idError);
        }

        User? user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<CitizenIdResponse>(AuthErrors.User.NotFound);
        }

        if (!user.IsActive)
        {
            return Result.Failure<CitizenIdResponse>(AuthErrors.Auth.InactiveAccount);
        }

        // Chỉ ghi nhận việc xem khi thật sự có CCCD; không bao giờ ghi số CCCD vào log.
        if (user.CitizenId is not null)
        {
            await LogAuditAsync(user.Id, user.Id.ToString(), "Profile_ViewCitizenId", "Success", cancellationToken);
        }

        return Result.Success(new CitizenIdResponse(user.CitizenId));
    }

    public async Task<Result> LinkExternalLoginAsync(
        Guid userId,
        string provider,
        string? idToken,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure(idError);
        }

        if (string.IsNullOrWhiteSpace(provider) || !string.Equals(provider, GoogleProvider, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.Auth.FirebaseProviderUnsupported);
        }

        Result<FirebaseIdentity> firebaseResult = await firebaseIdentityService.VerifyIdTokenAsync(idToken, cancellationToken);
        if (firebaseResult.IsFailure)
        {
            return Result.Failure(firebaseResult.Error);
        }

        FirebaseIdentity firebaseIdentity = firebaseResult.Value;
        if (!string.Equals(firebaseIdentity.SignInProvider, FirebaseGoogleSignInProvider, StringComparison.Ordinal))
        {
            return Result.Failure(AuthErrors.Auth.FirebaseProviderUnsupported);
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

        ExternalLogin? existingBySubject = await externalLoginRepository.FindAsync(
            GoogleProvider,
            firebaseIdentity.ProviderSubject,
            cancellationToken);
        if (existingBySubject is not null)
        {
            return Result.Failure(AuthErrors.ExternalLogin.AlreadyLinked);
        }

        IReadOnlyList<ExternalLogin> existingLogins = await externalLoginRepository.GetByUserAsync(userId, cancellationToken);
        if (existingLogins.Any(login => string.Equals(login.Provider, GoogleProvider, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure(AuthErrors.ExternalLogin.AlreadyLinked);
        }

        ExternalLogin login = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = GoogleProvider,
            ProviderSubject = firebaseIdentity.ProviderSubject,
            Email = firebaseIdentity.Email,
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        try
        {
            await externalLoginRepository.AddAsync(login, cancellationToken);
        }
        catch (StorageConstraintViolationException ex) when (
            ex.ViolationType == StorageConstraintViolationType.Unique &&
            (ex.ConstraintName is StorageConstraints.ExternalLoginProviderSubject or StorageConstraints.ExternalLoginUserProvider))
        {
            return Result.Failure(AuthErrors.ExternalLogin.AlreadyLinked);
        }

        await LogAuditAsync(user.Id, user.Id.ToString(), "ExternalLogin_Link", "Success", cancellationToken);
        return Result.Success();
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

        if (string.IsNullOrWhiteSpace(provider) || !string.Equals(provider, GoogleProvider, StringComparison.OrdinalIgnoreCase))
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
    }

    public async Task<Result> DeactivateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out Error? idError))
        {
            return Result.Failure(idError);
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
