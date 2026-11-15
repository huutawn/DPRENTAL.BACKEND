using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Auth;
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

        Result profileValidation = OrganizationProfilePolicy.ValidateForSubmission(organization, actor.CitizenId);
        if (profileValidation.IsFailure)
        {
            return Result.Failure<OrganizationRegisteredResponse>(profileValidation.Error);
        }

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

        LegalInfo requested = LegalInfo.From(request, actor.CitizenId, org.LegalRepresentativeCitizenId);
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

        Result profileValidation = OrganizationProfilePolicy.ValidateForSubmission(org, actor.CitizenId);
        if (profileValidation.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(profileValidation.Error);
        }

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
        public static LegalInfo From(UpdateOrganizationProfileRequest request, string? ownerCitizenId, string? existingCitizenId = null) =>
            new(
                request.Name.Trim(),
                request.TaxCode.Trim(),
                request.LegalRepresentativeName.Trim(),
                StringUtility.NullIfBlank(request.LegalRepresentativeCitizenId) ?? ownerCitizenId ?? existingCitizenId,
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

    public async Task<Result<OrganizationResponse>> SaveDraftAsync(
        Guid actorUserId,
        OrganizationDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.User.NotFound);
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

        if (request.Type is not (OrganizationType.Supplier or OrganizationType.Renter))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SelfRegistrationTypeNotAllowed);
        }

        string name = request.Name.Trim();
        if (name.Length == 0)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidName);
        }

        IReadOnlyList<OrganizationMembership> existing = await membershipRepository.GetMembershipsForUserAsync(actorUserId, cancellationToken);
        OrganizationMembership? activeMem = existing.FirstOrDefault(m => m.Status == MembershipStatus.Active);
        if (activeMem is not null && !string.Equals(activeMem.Role?.Name, SystemRoleCode.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.Forbidden);
        }

        // Verify media outside database transaction before row lock
        if (request.LogoMediaId.HasValue)
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

        if (request.BusinessLicenseDocumentMediaId.HasValue)
        {
            var docVerify = await mediaServiceClient.VerifyMediaAsync(request.BusinessLicenseDocumentMediaId.Value, cancellationToken);
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

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        if (activeMem is not null)
        {
            await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
            Organization? existingOrg = await organizationRepository.GetByIdForUpdateAsync(activeMem.OrganizationId, cancellationToken);
            if (existingOrg is null)
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
            }

            if (existingOrg.Status == OrganizationStatus.Pending)
            {
                return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_PENDING", "Hồ sơ đang trong quá trình xét duyệt, không thể sửa nháp."));
            }

            if (existingOrg.Status == OrganizationStatus.Active)
            {
                return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_ACTIVE", "Tổ chức đã được phê duyệt và đang hoạt động."));
            }

            if (existingOrg.Status == OrganizationStatus.Suspended)
            {
                return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_SUSPENDED", "Tổ chức đang bị khóa."));
            }

            // Cập nhật nháp cho organization đang là Draft hoặc Rejected
            existingOrg.Name = name;
            existingOrg.TaxCode = StringUtility.NullIfBlank(request.TaxCode);
            existingOrg.BusinessLicenseNumber = StringUtility.NullIfBlank(request.BusinessLicenseNumber);
            existingOrg.BusinessLicenseIssuedDate = request.BusinessLicenseIssuedDate;
            existingOrg.BusinessLicenseIssuedPlace = StringUtility.NullIfBlank(request.BusinessLicenseIssuedPlace);
            existingOrg.EstablishedDate = request.EstablishedDate;
            existingOrg.LegalRepresentativeName = StringUtility.NullIfBlank(request.LegalRepresentativeName);
            existingOrg.LegalRepresentativeCitizenId = StringUtility.NullIfBlank(request.LegalRepresentativeCitizenId) ?? actor.CitizenId;
            existingOrg.ContactEmail = string.IsNullOrWhiteSpace(request.ContactEmail) ? null : UserService.NormalizeEmail(request.ContactEmail);
            existingOrg.ContactPhone = StringUtility.NullIfBlank(request.ContactPhone);
            existingOrg.Address = StringUtility.NullIfBlank(request.Address);
            existingOrg.Province = StringUtility.NullIfBlank(request.Province);
            existingOrg.Ward = StringUtility.NullIfBlank(request.Ward);
            existingOrg.Website = StringUtility.NullIfBlank(request.Website);
            existingOrg.Description = StringUtility.NullIfBlank(request.Description);

            if (request.LogoMediaId.HasValue)
            {
                existingOrg.LogoMediaId = request.LogoMediaId.Value;
            }

            if (request.BusinessLicenseDocumentMediaId.HasValue)
            {
                existingOrg.BusinessLicenseDocumentMediaId = request.BusinessLicenseDocumentMediaId.Value;
            }

            existingOrg.Status = OrganizationStatus.Draft;
            // RejectionReason retains its value on existingOrg

            await organizationRepository.UpdateAsync(existingOrg, cancellationToken);
            await auditLogRepository.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = actor.Id,
                Target = existingOrg.Id.ToString(),
                Action = "Organization_SaveDraft",
                Result = "Success",
                Status = existingOrg.Status,
                CorrelationId = correlationIdProvider.GetCorrelationId(),
                Timestamp = timeProvider.GetUtcNow()
            }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            string? logoUrl = mediaUrlResolver.ResolvePublicUrl(existingOrg.LogoMediaId);
            return Result.Success(existingOrg.ToResponse(logoUrl, null, null, _reviewOptions));
        }

        // Tạo mới Draft
        Result<string> slug = await ResolveSlugAsync(request.Slug, name, cancellationToken);
        if (slug.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(slug.Error);
        }

        Role? ownerRole = await roleRepository.GetByNameAsync(SystemRoleCode.Owner, cancellationToken)
            ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);
        if (ownerRole is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.RoleNotFound);
        }

        Organization organization = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug.Value,
            Type = request.Type,
            Status = OrganizationStatus.Draft,
            CreatedAtUtc = now,
            TaxCode = StringUtility.NullIfBlank(request.TaxCode),
            BusinessLicenseNumber = StringUtility.NullIfBlank(request.BusinessLicenseNumber),
            BusinessLicenseIssuedDate = request.BusinessLicenseIssuedDate,
            BusinessLicenseIssuedPlace = StringUtility.NullIfBlank(request.BusinessLicenseIssuedPlace),
            EstablishedDate = request.EstablishedDate,
            LegalRepresentativeName = StringUtility.NullIfBlank(request.LegalRepresentativeName),
            LegalRepresentativeCitizenId = StringUtility.NullIfBlank(request.LegalRepresentativeCitizenId) ?? actor.CitizenId,
            ContactEmail = string.IsNullOrWhiteSpace(request.ContactEmail) ? null : UserService.NormalizeEmail(request.ContactEmail),
            ContactPhone = StringUtility.NullIfBlank(request.ContactPhone),
            Address = StringUtility.NullIfBlank(request.Address),
            Province = StringUtility.NullIfBlank(request.Province),
            Ward = StringUtility.NullIfBlank(request.Ward),
            Website = StringUtility.NullIfBlank(request.Website),
            LogoMediaId = request.LogoMediaId,
            Description = StringUtility.NullIfBlank(request.Description),
            BusinessLicenseDocumentMediaId = request.BusinessLicenseDocumentMediaId
        };

        OrganizationMembership ownerMembership = new()
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            OrganizationId = organization.Id,
            RoleId = ownerRole.Id,
            Status = MembershipStatus.Active,
            JoinedAtUtc = now
        };

        await using IExecutionTransaction newDraftTransaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        await organizationRepository.CreateAsync(organization, ownerMembership, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actor.Id,
            Target = organization.Id.ToString(),
            Action = "Organization_SaveDraft",
            Result = "Success",
            Status = organization.Status,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
        await newDraftTransaction.CommitAsync(cancellationToken);

        string? newLogoUrl = mediaUrlResolver.ResolvePublicUrl(organization.LogoMediaId);
        return Result.Success(organization.ToResponse(newLogoUrl, null, null, _reviewOptions));
    }

    public async Task<Result<OrganizationResponse>> SubmitDraftAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
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

        Organization? orgBefore = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (orgBefore is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (orgBefore.Status == OrganizationStatus.Pending)
        {
            return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_ALREADY_SUBMITTED", "Hồ sơ đã được nộp và đang chờ xét duyệt."));
        }

        if (orgBefore.Status == OrganizationStatus.Active)
        {
            return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_ACTIVE", "Tổ chức đã được phê duyệt."));
        }

        if (orgBefore.Status == OrganizationStatus.Suspended)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InactiveOrganization);
        }

        if (orgBefore.Status != OrganizationStatus.Draft && orgBefore.Status != OrganizationStatus.Rejected)
        {
            return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_INVALID_STATUS", "Chỉ có thể nộp duyệt hồ sơ ở trạng thái nháp hoặc bị từ chối."));
        }

        Result profileValidation = OrganizationProfilePolicy.ValidateForSubmission(orgBefore, actor.CitizenId);
        if (profileValidation.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(profileValidation.Error);
        }

        if (await organizationRepository.TaxCodeExistsAsync(orgBefore.TaxCode!, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.TaxCodeAlreadyExists);
        }

        // Verify media outside database transaction before row lock
        var docVerify = await mediaServiceClient.VerifyMediaAsync(orgBefore.BusinessLicenseDocumentMediaId!.Value, cancellationToken);
        if (docVerify.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(docVerify.Error);
        }
        var docValidation = MediaVerificationRules.ValidateDocument(docVerify.Value, actorUserId);
        if (docValidation.IsFailure)
        {
            return Result.Failure<OrganizationResponse>(docValidation.Error);
        }

        if (orgBefore.LogoMediaId.HasValue)
        {
            var logoVerify = await mediaServiceClient.VerifyMediaAsync(orgBefore.LogoMediaId.Value, cancellationToken);
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

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (org.Status != OrganizationStatus.Draft && org.Status != OrganizationStatus.Rejected)
        {
            return Result.Failure<OrganizationResponse>(Error.Conflict("ORGANIZATION_INVALID_STATUS", "Trạng thái hồ sơ đã bị thay đổi đồng thời."));
        }

        if (org.BusinessLicenseDocumentMediaId != orgBefore.BusinessLicenseDocumentMediaId ||
            org.LogoMediaId != orgBefore.LogoMediaId)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.ConcurrentModification);
        }

        bool isResubmitted = org.Status == OrganizationStatus.Rejected ||
                             org.ResubmittedAtUtc != null ||
                             org.ReviewedAtUtc != null ||
                             !string.IsNullOrEmpty(org.RejectionReason);

        org.Status = OrganizationStatus.Pending;
        org.LegalRepresentativeCitizenId = StringUtility.NullIfBlank(org.LegalRepresentativeCitizenId) ?? actor.CitizenId;
        org.RejectionReason = null;
        org.ReviewedByUserId = null;
        org.ReviewedAtUtc = null;
        org.LastRemindedAtUtc = null;

        if (isResubmitted)
        {
            org.ResubmittedAtUtc = now;
        }
        // Do NOT overwrite CreatedAtUtc!

        // Queue public visibility outbox inside transaction for logo; business license remains private
        if (org.LogoMediaId.HasValue)
        {
            await mediaOutboxService.QueuePublicVisibilityAsync(org.LogoMediaId.Value, cancellationToken);
        }

        await organizationRepository.UpdateAsync(org, cancellationToken);
        await auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actor.Id,
            Target = org.Id.ToString(),
            Action = isResubmitted ? "Organization_Resubmit" : "Organization_Submit",
            Result = "Success",
            Status = org.Status,
            CorrelationId = correlationIdProvider.GetCorrelationId(),
            Timestamp = timeProvider.GetUtcNow()
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        string? logoUrl = mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId);
        return Result.Success(org.ToResponse(logoUrl, null, null, _reviewOptions));
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
