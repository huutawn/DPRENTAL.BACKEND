using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Đọc thông tin organization. Admin xem mọi tổ chức (GET /organizations); user xem tổ chức của mình
/// qua <see cref="GetMyOrganizationsAsync"/>. MVP không có thành viên — mỗi tổ chức chỉ có Owner.
/// </summary>
public sealed class OrganizationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IRoleRepository roleRepository,
    IMediaUrlResolver mediaUrlResolver,
    IOptions<OrganizationReviewOptions>? reviewOptions = null,
    TimeProvider? timeProvider = null) : IOrganizationService
{
    private readonly OrganizationReviewOptions _reviewOptions = reviewOptions?.Value ?? new OrganizationReviewOptions();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        // Hai đối tượng được xem, mỗi bên một điều kiện. Người khác nhận cùng một lỗi dù tổ chức
        // đang ở trạng thái nào, để không lộ tổ chức đang chờ duyệt hay bị khoá.
        if (!await CanPlatformReadAsync(actorUserId, cancellationToken) &&
            !await IsActiveOwnerAsync(organizationId, actorUserId, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
        }

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

    public async Task<Result<OrganizationResponse?>> GetMineAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(userId, nameof(userId), out _))
        {
            return Result.Failure<OrganizationResponse?>(AuthErrors.User.NotFound);
        }

        IReadOnlyList<OrganizationMembership> memberships = await membershipRepository.GetMembershipsForUserAsync(userId, cancellationToken);

        // Dữ liệu tạo trước khi có quy tắc 1-1 có thể còn nhiều tổ chức cho một tài khoản: lấy hồ sơ nộp gần nhất.
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

        // User thường xem tổ chức của mình qua GET /users/me/organizations.
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

    public async Task<Result> UpdateAsync(
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

        if (request is null || (request.Name is null && request.IsActive is null))
        {
            return Result.Failure(AuthErrors.Organization.EmptyPatch);
        }

        string? trimmedName = request.Name?.Trim();
        if (trimmedName is not null && (trimmedName.Length == 0 || trimmedName.Length > 150))
        {
            return Result.Failure(AuthErrors.Organization.InvalidName);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (request.IsActive.HasValue && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        if (org.Status != OrganizationStatus.Active && !canManageSuspended)
        {
            return Result.Failure(InactiveError(org));
        }

        bool isPlatformEditor = globalPerms.Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal);
        if (!isPlatformEditor)
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Organization.Forbidden);
            }
        }

        bool nameChanged = trimmedName is not null && !string.Equals(trimmedName, org.Name, StringComparison.Ordinal);
        bool activeChanged = request.IsActive.HasValue && request.IsActive.Value != (org.Status == OrganizationStatus.Active);

        // Tên là thông tin pháp lý Platform đã xác minh khi duyệt — Owner không tự đổi, giống PUT /profile.
        if (nameChanged && !isPlatformEditor)
        {
            return Result.Failure(AuthErrors.Organization.LegalInfoLocked);
        }

        if (!nameChanged && !activeChanged)
        {
            return Result.Failure(AuthErrors.Organization.NoOpPatch);
        }

        OrganizationStatus? newStatus = null;
        if (activeChanged)
        {
            // Pending -> Active chỉ đi qua PATCH /approve để luôn ghi lại người duyệt.
            newStatus = (request.IsActive!.Value, org.Status) switch
            {
                (true, OrganizationStatus.Suspended) => OrganizationStatus.Active,
                (false, OrganizationStatus.Active) => OrganizationStatus.Suspended,
                _ => null
            };

            if (newStatus is null)
            {
                return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
            }
        }

        if (nameChanged)
        {
            org.Name = trimmedName!;
        }

        if (newStatus.HasValue)
        {
            org.Status = newStatus.Value;
        }

        await organizationRepository.UpdateAsync(org, cancellationToken);
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

    /// <summary>Platform (admin, ops) xem được mọi tổ chức ở mọi trạng thái.</summary>
    private async Task<bool> CanPlatformReadAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetGlobalPermissionsAsync(userId, cancellationToken)).Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal);

    /// <summary>Owner xem được tổ chức của mình ở mọi trạng thái, để theo dõi kết quả duyệt và sửa hồ sơ.</summary>
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

    private async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(userId, cancellationToken);
        IReadOnlyList<string> scopedPerms = await membershipRepository.GetScopedPermissionCodesAsync(userId, organizationId, cancellationToken);
        return globalPerms.Concat(scopedPerms).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Error InactiveError(Organization org) =>
        org.Status is OrganizationStatus.Pending or OrganizationStatus.Rejected
            ? AuthErrors.Organization.PendingApproval
            : AuthErrors.Organization.InactiveOrganization;

    private static bool IsOwner(Role? role) =>
        role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
}
