using Dprental.AuthService.Common.Utils;
using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Quản lý organization: đọc chi tiết, danh sách, đổi tên, khóa và mở khóa.
/// Admin thao tác qua /admin/organizations; Owner xem tổ chức của mình qua GET /organizations/{id} và /mine.
/// </summary>
public sealed class OrganizationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IRoleRepository roleRepository,
    IMediaUrlResolver mediaUrlResolver,
    IAuditLogRepository auditLogRepository,
    ICorrelationIdProvider correlationIdProvider,
    IMediaServiceClient mediaServiceClient,
    IMediaOutboxService mediaOutboxService,
    IOptions<OrganizationReviewOptions>? reviewOptions = null,
    TimeProvider? timeProvider = null) : IOrganizationService
{
    private readonly IAuditLogRepository _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
    private readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider ?? throw new ArgumentNullException(nameof(correlationIdProvider));
    private readonly IMediaServiceClient _mediaServiceClient = mediaServiceClient ?? throw new ArgumentNullException(nameof(mediaServiceClient));
    private readonly IMediaOutboxService _mediaOutboxService = mediaOutboxService ?? throw new ArgumentNullException(nameof(mediaOutboxService));
    private readonly OrganizationReviewOptions _reviewOptions = reviewOptions?.Value ?? new OrganizationReviewOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    /// <summary>Lấy chi tiết organization khi người gọi là Owner (GET /organizations/{id}).</summary>
    public async Task<Result<OrganizationResponse>> GetByIdAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!await IsActiveOwnerAsync(organizationId, actorUserId, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
        }

        return await BuildOrganizationResponseAsync(org, cancellationToken);
    }

    /// <summary>Lấy chi tiết organization dành cho Admin (GET /admin/organizations/{id}).</summary>
    public async Task<Result<OrganizationResponse>> GetAdminByIdAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!await CanPlatformReadAsync(actorUserId, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.PlatformAdminRequired);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        return await BuildOrganizationResponseAsync(org, cancellationToken);
    }

    public async Task<Result<OrganizationResponse?>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out _))
        {
            return Result.Failure<OrganizationResponse?>(AuthErrors.User.NotFound);
        }

        IReadOnlyList<OrganizationMembership> memberships = await membershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

        Organization? org = memberships
            .Where(m => m.Status == MembershipStatus.Active && m.Organization is not null)
            .Select(m => m.Organization)
            .OrderByDescending(OrganizationReviewSchedule.SubmittedAtUtc)
            .ThenBy(o => o.Id)
            .FirstOrDefault();

        return org is null
            ? Result.Success<OrganizationResponse?>(null)
            : Result.Success<OrganizationResponse?>(
                org.ToResponse(mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId), reviewOptions: _reviewOptions));
    }

    public async Task<Result<PagedResult<OrganizationSummaryResponse>>> ListAsync(
        Guid actorUserId,
        PageRequest request,
        OrganizationStatus? status = null,
        OrganizationType? type = null,
        string? province = null,
        bool overdueOnly = false,
        bool remindedOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(AuthErrors.Paging.InvalidPage);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(validation.Error);
        }

        if (status.HasValue && !DtoValidator.TryValidateEnum(status.Value, nameof(status), out Error? statusError))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(statusError);
        }

        if (type.HasValue && !DtoValidator.TryValidateEnum(type.Value, nameof(type), out Error? typeError))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(typeError);
        }

        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(AuthErrors.Organization.UserNotFound);
        }

        if (!await CanPlatformReadAsync(actorUserId, cancellationToken))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(AuthErrors.Organization.PlatformAdminRequired);
        }

        DateTime utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime overdueCutoffUtc = utcNow.AddHours(-_reviewOptions.SlaHours);

        PagedResult<Organization> paged = await organizationRepository.GetAllAsync(
            request,
            status,
            type,
            province,
            overdueOnly,
            remindedOnly,
            overdueCutoffUtc,
            cancellationToken);

        var items = paged.Items
            .Select(org => org.ToSummaryResponse(mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId), _reviewOptions, utcNow))
            .ToList();
        return Result.Success(new PagedResult<OrganizationSummaryResponse>(items, paged.Page, paged.PageSize, paged.TotalCount));
    }

    /// <summary>Cập nhật hồ sơ liên hệ và hiển thị của organization dành cho Admin (PUT /admin/organizations/{id}).</summary>
    public async Task<Result> UpdateProfileAsync(
        Guid actorUserId,
        Guid organizationId,
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request is null)
        {
            return Result.Failure(AuthErrors.Organization.EmptyPatch);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        string trimmedName = request.Name.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > 150)
        {
            return Result.Failure(AuthErrors.Organization.InvalidName);
        }

        if (!await CanPlatformUpdateAsync(actorUserId, cancellationToken))
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        Organization? before = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (before is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        // Remote media verification must finish before opening the row-lock transaction.
        if (request.LogoMediaId.HasValue && request.LogoMediaId != before.LogoMediaId)
        {
            var logoVerify = await _mediaServiceClient.VerifyMediaAsync(request.LogoMediaId.Value, cancellationToken);
            if (logoVerify.IsFailure)
            {
                return Result.Failure(logoVerify.Error);
            }
            var logoValidation = MediaVerificationRules.ValidateImage(logoVerify.Value, actorUserId);
            if (logoValidation.IsFailure)
            {
                return Result.Failure(logoValidation.Error);
            }
        }

        string normalizedEmail = UserService.NormalizeEmail(request.ContactEmail);
        string normalizedPhone = request.ContactPhone.Trim();
        string normalizedAddress = request.Address.Trim();
        string normalizedProvince = request.Province.Trim();
        string normalizedWard = request.Ward.Trim();
        string? normalizedWebsite = StringUtility.NullIfBlank(request.Website);
        Guid? normalizedLogo = request.LogoMediaId;
        string? normalizedDescription = StringUtility.NullIfBlank(request.Description);

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (org.LogoMediaId != before.LogoMediaId)
        {
            return Result.Failure(AuthErrors.Organization.ConcurrentModification);
        }

        bool hasChanges =
            !string.Equals(org.Name, trimmedName, StringComparison.Ordinal) ||
            !string.Equals(org.ContactEmail, normalizedEmail, StringComparison.Ordinal) ||
            !string.Equals(org.ContactPhone, normalizedPhone, StringComparison.Ordinal) ||
            !string.Equals(org.Address, normalizedAddress, StringComparison.Ordinal) ||
            !string.Equals(org.Province, normalizedProvince, StringComparison.Ordinal) ||
            !string.Equals(org.Ward, normalizedWard, StringComparison.Ordinal) ||
            !string.Equals(org.Website, normalizedWebsite, StringComparison.Ordinal) ||
            org.LogoMediaId != normalizedLogo ||
            !string.Equals(org.Description, normalizedDescription, StringComparison.Ordinal);

        if (!hasChanges)
        {
            return Result.Success();
        }

        Guid? oldLogoId = org.LogoMediaId;
        org.Name = trimmedName;
        org.ContactEmail = normalizedEmail;
        org.ContactPhone = normalizedPhone;
        org.Address = normalizedAddress;
        org.Province = normalizedProvince;
        org.Ward = normalizedWard;
        org.Website = normalizedWebsite;
        org.LogoMediaId = normalizedLogo;
        org.Description = normalizedDescription;

        if (normalizedLogo.HasValue && normalizedLogo != oldLogoId)
        {
            await _mediaOutboxService.QueuePublicVisibilityAsync(normalizedLogo.Value, cancellationToken);
        }

        await organizationRepository.UpdateAsync(org, cancellationToken);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = "Organization_ProfileUpdate",
            Result = "Success",
            Reason = null,
            Status = org.Status,
            CorrelationId = _correlationIdProvider.GetCorrelationId(),
            Timestamp = _timeProvider.GetUtcNow()
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Khóa organization dành cho Admin (POST /admin/organizations/{id}/suspend).</summary>
    public async Task<Result> SuspendAsync(
        Guid actorUserId,
        Guid organizationId,
        SuspendOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request is null)
        {
            return Result.Failure(Error.Validation("Validation.Reason", "Lý do khóa không được để trống."));
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        string trimmedReason = request.Reason.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason) || trimmedReason.Length > 500)
        {
            return Result.Failure(Error.Validation("Validation.Reason", "Lý do khóa không hợp lệ."));
        }

        if (!await CanPlatformSuspendAsync(actorUserId, cancellationToken))
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (org.Status == OrganizationStatus.Suspended)
        {
            return Result.Success();
        }

        if (org.Status != OrganizationStatus.Active)
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        org.Status = OrganizationStatus.Suspended;
        org.SuspensionReason = trimmedReason;
        await organizationRepository.UpdateAsync(org, cancellationToken);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = "Organization_Suspend",
            Result = "Success",
            Reason = trimmedReason,
            Status = org.Status,
            CorrelationId = _correlationIdProvider.GetCorrelationId(),
            Timestamp = _timeProvider.GetUtcNow()
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Mở khóa organization dành cho Admin (POST /admin/organizations/{id}/activate).</summary>
    public async Task<Result> ActivateAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!await CanPlatformSuspendAsync(actorUserId, cancellationToken))
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (org.Status == OrganizationStatus.Active)
        {
            return Result.Success();
        }

        if (org.Status != OrganizationStatus.Suspended)
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        org.Status = OrganizationStatus.Active;
        org.SuspensionReason = null;
        await organizationRepository.UpdateAsync(org, cancellationToken);

        await _auditLogRepository.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorUserId,
            Target = org.Id.ToString(),
            Action = "Organization_Activate",
            Result = "Success",
            Reason = null,
            Status = org.Status,
            CorrelationId = _correlationIdProvider.GetCorrelationId(),
            Timestamp = _timeProvider.GetUtcNow()
        }, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Lấy nhật ký lịch sử của organization có phân trang và bộ lọc tìm kiếm dành cho Admin (GET /admin/organizations/{id}/history).</summary>
    public async Task<Result<PagedResult<OrganizationHistoryResponse>>> GetHistoryAsync(
        Guid actorUserId,
        Guid organizationId,
        PageRequest request,
        string? action = null,
        OrganizationStatus? status = null,
        string? result = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request is null)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Paging.InvalidPage);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(validation.Error);
        }

        if (status.HasValue && !DtoValidator.TryValidateEnum(status.Value, nameof(status), out Error? statusError))
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(statusError);
        }

        if (action?.Trim().Length > PageRequest.MaxSearchLength)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Paging.InvalidSearch);
        }

        if (result?.Trim().Length > PageRequest.MaxSearchLength)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Paging.InvalidSearch);
        }

        if (reason?.Trim().Length > PageRequest.MaxSearchLength)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Paging.InvalidSearch);
        }

        if (!await CanPlatformReadAsync(actorUserId, cancellationToken))
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Organization.PlatformAdminRequired);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<PagedResult<OrganizationHistoryResponse>>(AuthErrors.Organization.OrganizationNotFound);
        }

        PagedResult<AuditLog> paged = await _auditLogRepository.GetOrganizationHistoryAsync(
            organizationId,
            request,
            action,
            status,
            result,
            reason,
            cancellationToken);
        var items = paged.Items.Select(l => l.ToHistoryResponse()).ToList();
        return Result.Success(new PagedResult<OrganizationHistoryResponse>(items, paged.Page, paged.PageSize, paged.TotalCount));
    }

    public async Task<Result<IReadOnlyList<MyOrganizationResponse>>> GetMyOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out _))
        {
            return Result.Failure<IReadOnlyList<MyOrganizationResponse>>(AuthErrors.User.NotFound);
        }

        IReadOnlyList<OrganizationMembership> memberships = await membershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

        IReadOnlyList<MyOrganizationResponse> result = memberships
            .Where(m => m.Status == MembershipStatus.Active)
            .OrderBy(m => m.JoinedAtUtc)
            .ThenBy(m => m.Id)
            .Select(m => new MyOrganizationResponse(
                m.Id,
                m.OrganizationId,
                m.Organization?.Name ?? string.Empty,
                m.Organization?.Slug ?? string.Empty,
                m.Organization?.Type ?? OrganizationType.Supplier,
                m.Organization?.Status ?? OrganizationStatus.Active,
                m.Role?.Name ?? string.Empty,
                m.Status,
                m.JoinedAtUtc,
                m.Organization?.SuspensionReason))
            .ToList();

        return Result.Success(result);
    }

    private async Task<Result<OrganizationResponse>> BuildOrganizationResponseAsync(
        Organization org,
        CancellationToken cancellationToken)
    {
        string? logoUrl = mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId);
        string? licenseUrl = null;
        DateTime? licenseExpires = null;
        if (org.BusinessLicenseDocumentMediaId.HasValue)
        {
            var accessResult = await mediaUrlResolver.ResolvePrivateAccessUrlAsync(org.BusinessLicenseDocumentMediaId.Value, cancellationToken);
            if (accessResult.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Media.ServiceUnavailable);
            }
            licenseUrl = accessResult.Value.Url;
            licenseExpires = accessResult.Value.ExpiresAtUtc.UtcDateTime;
        }

        return Result.Success(org.ToResponse(logoUrl, licenseUrl, licenseExpires, _reviewOptions));
    }

    private async Task<bool> CanPlatformReadAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetGlobalPermissionsAsync(userId, cancellationToken)).Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal);

    private async Task<bool> CanPlatformUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetGlobalPermissionsAsync(userId, cancellationToken)).Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal);

    private async Task<bool> CanPlatformSuspendAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetGlobalPermissionsAsync(userId, cancellationToken)).Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

    private async Task<bool> IsActiveOwnerAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken) =>
        await membershipRepository.GetByOrganizationAndUserAsync(organizationId, userId, cancellationToken) is { Status: MembershipStatus.Active } membership
        && IsOwner(membership.Role);

    private async Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> perms = await membershipRepository.GetGlobalPermissionsAsync(userId, cancellationToken);
        if (perms.Count > 0)
        {
            return perms;
        }
        return await roleRepository.GetUserPermissionCodesAsync(userId, cancellationToken);
    }

    private static bool IsOwner(Role? role) =>
        role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
}
