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
    IAuditLogRepository? auditLogRepository = null,
    ICorrelationIdProvider? correlationIdProvider = null,
    IOptions<OrganizationReviewOptions>? reviewOptions = null,
    TimeProvider? timeProvider = null) : IOrganizationService
{
    private readonly IAuditLogRepository? _auditLogRepository = auditLogRepository;
    private readonly ICorrelationIdProvider? _correlationIdProvider = correlationIdProvider;
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

        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(AuthErrors.Organization.UserNotFound);
        }

        if (!await CanPlatformReadAsync(actorUserId, cancellationToken))
        {
            return Result.Failure<PagedResult<OrganizationSummaryResponse>>(AuthErrors.Organization.PlatformAdminRequired);
        }

        PagedResult<Organization> paged = await organizationRepository.GetAllAsync(request, status, cancellationToken);

        DateTime utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var items = paged.Items
            .Select(org => org.ToSummaryResponse(mediaUrlResolver.ResolvePublicUrl(org.LogoMediaId), _reviewOptions, utcNow))
            .ToList();
        return Result.Success(new PagedResult<OrganizationSummaryResponse>(items, paged.Page, paged.PageSize, paged.TotalCount));
    }

    /// <summary>Đổi tên organization dành cho Admin (PUT /admin/organizations/{id}).</summary>
    public async Task<Result> UpdateNameAsync(
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

        string? trimmedName = request.Name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > 150)
        {
            return Result.Failure(AuthErrors.Organization.InvalidName);
        }

        if (!await CanPlatformUpdateAsync(actorUserId, cancellationToken))
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        await using IExecutionTransaction transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
        Organization? org = await organizationRepository.GetByIdForUpdateAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        bool nameChanged = !string.Equals(trimmedName, org.Name, StringComparison.Ordinal);
        if (!nameChanged)
        {
            return Result.Success();
        }

        org.Name = trimmedName!;
        await organizationRepository.UpdateAsync(org, cancellationToken);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = actorUserId,
                Target = org.Id.ToString(),
                Action = "Organization_Rename",
                Result = "Success",
                CorrelationId = _correlationIdProvider?.GetCorrelationId() ?? string.Empty,
                Timestamp = _timeProvider.GetUtcNow()
            }, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Khóa organization dành cho Admin (POST /admin/organizations/{id}/suspend).</summary>
    public async Task<Result> SuspendAsync(
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

        if (org.Status == OrganizationStatus.Suspended)
        {
            return Result.Success();
        }

        if (org.Status != OrganizationStatus.Active)
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        org.Status = OrganizationStatus.Suspended;
        await organizationRepository.UpdateAsync(org, cancellationToken);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = actorUserId,
                Target = org.Id.ToString(),
                Action = "Organization_Suspend",
                Result = "Success",
                CorrelationId = _correlationIdProvider?.GetCorrelationId() ?? string.Empty,
                Timestamp = _timeProvider.GetUtcNow()
            }, cancellationToken);
        }

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
        await organizationRepository.UpdateAsync(org, cancellationToken);

        if (_auditLogRepository is not null)
        {
            await _auditLogRepository.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = actorUserId,
                Target = org.Id.ToString(),
                Action = "Organization_Activate",
                Result = "Success",
                CorrelationId = _correlationIdProvider?.GetCorrelationId() ?? string.Empty,
                Timestamp = _timeProvider.GetUtcNow()
            }, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Result.Success();
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
                m.JoinedAtUtc))
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
