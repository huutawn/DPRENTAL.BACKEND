using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Common.Utils;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.Services;

public sealed class OrganizationRegistrationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    IAuditLogRepository auditLogRepository,
    TimeProvider timeProvider,
    ICorrelationIdProvider correlationIdProvider,
    IMediaServiceClient mediaServiceClient,
    IMediaUrlResolver mediaUrlResolver,
    IMediaOutboxService mediaOutboxService,
    IOptions<OrganizationReviewOptions>? reviewOptions = null) : IOrganizationRegistrationService
{
    private readonly OrganizationReviewOptions _reviewOptions = reviewOptions?.Value ?? new OrganizationReviewOptions();
    private static readonly Guid SeededOwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private const int MaxSlugAttempts = 5;

    public async Task<Result<OrganizationRegisteredResponse>> RegisterForExistingUserAsync(
        Guid actorUserId,
        OrganizationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out Error? idError))
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.User.NotFound);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<OrganizationRegisteredResponse>(validationError);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.User.NotFound);
        }

        if (ProfileCompletionGuard.Check(actor) is Error profileError)
        {
            return Result.Failure<OrganizationRegisteredResponse>(profileError);
        }

        // Quan hệ 1-1: một tài khoản chỉ có một tổ chức, kể cả khi hồ sơ đang chờ hay bị từ chối (khi đó sửa hồ sơ cũ).
        IReadOnlyList<OrganizationMembership> existing = await membershipRepository.GetMembershipsForUserAsync(actorUserId, cancellationToken);
        if (existing.Any(m => m.Status == MembershipStatus.Active))
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.AlreadyRegistered);
        }

        if (request.Type is not (OrganizationType.Supplier or OrganizationType.Renter))
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.SelfRegistrationTypeNotAllowed);
        }

        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.InvalidName);
        }

        string taxCode = request.TaxCode.Trim();
        if (await organizationRepository.TaxCodeExistsAsync(taxCode, cancellationToken))
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.TaxCodeAlreadyExists);
        }

        Result<string> slug = await ResolveSlugAsync(request.Slug, name, cancellationToken);
        if (slug.IsFailure)
        {
            return Result.Failure<OrganizationRegisteredResponse>(slug.Error);
        }

        Role? ownerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Owner, cancellationToken)
            ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);
        if (ownerRole is null)
        {
            return Result.Failure<OrganizationRegisteredResponse>(AuthErrors.Organization.RoleNotFound);
        }
        if (request.LogoMediaId.HasValue)
        {
            var logoVerify = await mediaServiceClient.VerifyMediaAsync(request.LogoMediaId.Value, cancellationToken);
            if (logoVerify.IsFailure)
            {
                return Result.Failure<OrganizationRegisteredResponse>(logoVerify.Error);
            }
            var logoValidation = MediaVerificationRules.ValidateImage(logoVerify.Value, actorUserId);
            if (logoValidation.IsFailure)
            {
                return Result.Failure<OrganizationRegisteredResponse>(logoValidation.Error);
            }
        }

        var docVerify = await mediaServiceClient.VerifyMediaAsync(request.BusinessLicenseDocumentMediaId, cancellationToken);
        if (docVerify.IsFailure)
        {
            return Result.Failure<OrganizationRegisteredResponse>(docVerify.Error);
        }
        var docValidation = MediaVerificationRules.ValidateDocument(docVerify.Value, actorUserId);
        if (docValidation.IsFailure)
        {
            return Result.Failure<OrganizationRegisteredResponse>(docValidation.Error);
        }


        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        Organization organization = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.Value,
            Type = request.Type,
            Status = OrganizationStatus.Pending,
            CreatedAtUtc = now,
            TaxCode = taxCode,
            BusinessLicenseNumber = StringUtility.NullIfBlank(request.BusinessLicenseNumber),
            BusinessLicenseIssuedDate = request.BusinessLicenseIssuedDate,
            BusinessLicenseIssuedPlace = StringUtility.NullIfBlank(request.BusinessLicenseIssuedPlace),
            EstablishedDate = request.EstablishedDate,
            LegalRepresentativeName = request.LegalRepresentativeName.Trim(),
            LegalRepresentativeCitizenId = StringUtility.NullIfBlank(request.LegalRepresentativeCitizenId)
                ?? actor.CitizenId,
            ContactEmail = UserService.NormalizeEmail(request.ContactEmail),
            ContactPhone = request.ContactPhone.Trim(),
            Address = request.Address.Trim(),
            Province = request.Province.Trim(),
            Ward = request.Ward.Trim(),
            Website = StringUtility.NullIfBlank(request.Website),
            LogoMediaId = request.LogoMediaId,
            Description = StringUtility.NullIfBlank(request.Description),
            BusinessLicenseDocumentMediaId = request.BusinessLicenseDocumentMediaId
        };

        // Membership Owner Active ngay, nhưng org Pending nên chưa đóng góp permission/organization context vào JWT.
        OrganizationMembership ownerMembership = new()
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            OrganizationId = organization.Id,
            RoleId = ownerRole.Id,
            Status = MembershipStatus.Active,
            JoinedAtUtc = now
        };
        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        if (request.LogoMediaId.HasValue)
        {
            await mediaOutboxService.QueuePublicVisibilityAsync(request.LogoMediaId.Value, cancellationToken);
        }
        await organizationRepository.CreateAsync(organization, ownerMembership, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actor.Id,
            Target = organization.Id.ToString(),
            Action = "Organization_Register",
            Result = "Success",
            Reason = null,
            Status = organization.Status,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(new OrganizationRegisteredResponse(actor.Id, organization.Id, organization.Slug, organization.Status));
    }

    public async Task<Result<OrganizationResponse>> UpdateProfileAsync(
        Guid actorUserId,
        Guid organizationId,
        UpdateOrganizationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<OrganizationResponse>(validationError);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.User.NotFound);
        }

        OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
        if (membership is not { Status: MembershipStatus.Active } ||
            !string.Equals(membership.Role?.Name, SystemRoleCode.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.Forbidden);
        }

        Organization? before = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (before is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        // Remote verification must finish before opening the row-lock transaction.
        if (request.LogoMediaId.HasValue && request.LogoMediaId != before.LogoMediaId)
        {
            var logoVerify = await mediaServiceClient.VerifyMediaAsync(request.LogoMediaId.Value, cancellationToken);
            if (logoVerify.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(logoVerify.Error);
            }
            var logoValidation = MediaVerificationRules.ValidateImage(logoVerify.Value, actorUserId);
            if (logoValidation.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(logoValidation.Error);
            }
        }

        if (request.BusinessLicenseDocumentMediaId != before.BusinessLicenseDocumentMediaId)
        {
            var docVerify = await mediaServiceClient.VerifyMediaAsync(request.BusinessLicenseDocumentMediaId, cancellationToken);
            if (docVerify.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(docVerify.Error);
            }
            var docValidation = MediaVerificationRules.ValidateDocument(docVerify.Value, actorUserId);
            if (docValidation.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(docValidation.Error);
            }
        }

        var accessResult = await mediaUrlResolver.ResolvePrivateAccessUrlAsync(request.BusinessLicenseDocumentMediaId, cancellationToken);
        if (accessResult.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(accessResult.Error);
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }
        if (org.LogoMediaId != before.LogoMediaId ||
            org.BusinessLicenseDocumentMediaId != before.BusinessLicenseDocumentMediaId)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.ConcurrentModification);
        }

        if (org.Status == OrganizationStatus.Suspended)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InactiveOrganization);
        }

        LegalInfo requested = LegalInfo.From(request, actor.CitizenId);
        if (org.Status == OrganizationStatus.Active && requested != LegalInfo.From(org))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.LegalInfoLocked);
        }

        if (!string.Equals(requested.TaxCode, org.TaxCode, StringComparison.Ordinal) &&
            await organizationRepository.TaxCodeExistsAsync(requested.TaxCode, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.TaxCodeAlreadyExists);
        }

        Guid? oldLogoId = org.LogoMediaId;
        requested.ApplyTo(org);
        org.ContactEmail = UserService.NormalizeEmail(request.ContactEmail);
        org.ContactPhone = request.ContactPhone.Trim();
        org.Address = request.Address.Trim();
        org.Province = request.Province.Trim();
        org.Ward = request.Ward.Trim();
        org.Website = StringUtility.NullIfBlank(request.Website);
        org.LogoMediaId = request.LogoMediaId;
        org.Description = StringUtility.NullIfBlank(request.Description);

        if (request.LogoMediaId.HasValue && request.LogoMediaId != oldLogoId)
        {
            await mediaOutboxService.QueuePublicVisibilityAsync(request.LogoMediaId.Value, cancellationToken);
        }

        bool resubmitted = org.Status == OrganizationStatus.Rejected;
        if (resubmitted)
        {
            // Gửi lại để duyệt vòng mới; lý do và người duyệt cũ vẫn còn trong audit log.
            org.Status = OrganizationStatus.Pending;
            org.RejectionReason = null;
            org.ReviewedByUserId = null;
            org.ReviewedAtUtc = null;
            // Vòng duyệt mới: tính lại hạn duyệt từ lúc gửi lại, bỏ lần nhắc của vòng trước.
            org.ResubmittedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            org.LastRemindedAtUtc = null;
        }

        await organizationRepository.UpdateAsync(org, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = resubmitted ? "Organization_Resubmit" : "Organization_ProfileUpdate",
            Result = "Success",
            Status = org.Status,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        string? logoUrl = mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId);
        return Result.Success(org.ToResponse(logoUrl, accessResult.Value.Url, accessResult.Value.ExpiresAtUtc.UtcDateTime, _reviewOptions));
    }

    public async Task<Result<OrganizationResponse>> RemindReviewAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
        if (membership is not { Status: MembershipStatus.Active } ||
            !string.Equals(membership.Role?.Name, SystemRoleCode.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.Forbidden);
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        // Chỉ hồ sơ đang chờ mới có hạn duyệt; CanRemindAtUtc trả null cho mọi trạng thái khác.
        if (OrganizationReviewSchedule.CanRemindAtUtc(org, _reviewOptions) is not DateTime canRemindAtUtc)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.NotPendingReview);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (now.UtcDateTime < canRemindAtUtc)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.ReviewReminderTooEarly);
        }

        org.LastRemindedAtUtc = now.UtcDateTime;
        await organizationRepository.UpdateAsync(org, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = "Organization_ReviewReminder",
            Result = "Success",
            Status = org.Status,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = now
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Result.Success(org.ToResponse(mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId), reviewOptions: _reviewOptions));
    }

    /// <summary>Các trường Platform đã xác minh khi duyệt; so sánh theo giá trị (record) để biết Owner có đổi không.</summary>
    private sealed record LegalInfo(
        string Name,
        string TaxCode,
        string? LegalRepresentativeName,
        string? LegalRepresentativeCitizenId,
        string? BusinessLicenseNumber,
        DateOnly? BusinessLicenseIssuedDate,
        string? BusinessLicenseIssuedPlace,
        DateOnly? EstablishedDate,
        Guid BusinessLicenseDocumentMediaId)
    {
        public static LegalInfo From(UpdateOrganizationProfileRequest request, string? ownerCitizenId) =>
            new(
                request.Name.Trim(),
                request.TaxCode.Trim(),
                request.LegalRepresentativeName.Trim(),
                StringUtility.NullIfBlank(request.LegalRepresentativeCitizenId) ?? ownerCitizenId,
                StringUtility.NullIfBlank(request.BusinessLicenseNumber),
                request.BusinessLicenseIssuedDate,
                StringUtility.NullIfBlank(request.BusinessLicenseIssuedPlace),
                request.EstablishedDate,
                request.BusinessLicenseDocumentMediaId);

        public static LegalInfo From(Organization org) =>
            new(
                org.Name,
                org.TaxCode ?? string.Empty,
                org.LegalRepresentativeName,
                org.LegalRepresentativeCitizenId,
                org.BusinessLicenseNumber,
                org.BusinessLicenseIssuedDate,
                org.BusinessLicenseIssuedPlace,
                org.EstablishedDate,
                org.BusinessLicenseDocumentMediaId ?? Guid.Empty);

        public void ApplyTo(Organization org)
        {
            org.Name = Name;
            org.TaxCode = TaxCode;
            org.LegalRepresentativeName = LegalRepresentativeName;
            org.LegalRepresentativeCitizenId = LegalRepresentativeCitizenId;
            org.BusinessLicenseNumber = BusinessLicenseNumber;
            org.BusinessLicenseIssuedDate = BusinessLicenseIssuedDate;
            org.BusinessLicenseIssuedPlace = BusinessLicenseIssuedPlace;
            org.EstablishedDate = EstablishedDate;
            org.BusinessLicenseDocumentMediaId = BusinessLicenseDocumentMediaId;
        }
    }

    /// <summary>Slug do người dùng nhập phải hợp lệ và chưa bị dùng; slug tự sinh sẽ được thêm hậu tố khi trùng.</summary>
    private async Task<Result<string>> ResolveSlugAsync(string? requestedSlug, string name, CancellationToken cancellationToken)
    {
        string? trimmed = requestedSlug?.Trim();

        if (trimmed is not null && trimmed.Length > 0)
        {
            if (!OrganizationSlug.IsCanonical(trimmed))
            {
                return AuthErrors.Organization.InvalidSlugFormat;
            }

            if (await organizationRepository.SlugExistsAsync(trimmed, cancellationToken))
            {
                return AuthErrors.Organization.SlugAlreadyExists;
            }

            return trimmed;
        }

        string baseSlug = OrganizationSlug.FromName(name);
        string candidate = baseSlug;

        for (int attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            if (!await organizationRepository.SlugExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }

            candidate = OrganizationSlug.WithRandomSuffix(baseSlug);
        }

        return candidate;
    }
}
