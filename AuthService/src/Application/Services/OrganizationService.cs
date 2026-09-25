using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Mapping;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Quản lý organization, thành viên và lời mời.
/// Một implementation duy nhất, chia file theo trách nhiệm quản lý tổ chức, thành viên và lời mời.
/// </summary>
public sealed partial class OrganizationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<OrganizationService> logger) : IOrganizationService
{
    private static readonly Guid SeededOwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    public async Task<Result<OrganizationResponse>> CreateAsync(
        Guid actorUserId,
        CreateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out Error? actorError))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.UserNotFound);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<OrganizationResponse>(validationError);
        }

        string trimmedName = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidName);
        }

        string trimmedSlug = request.Slug.Trim();
        if (string.IsNullOrWhiteSpace(trimmedSlug) || !OrganizationSlug.IsCanonical(trimmedSlug))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidSlugFormat);
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidStateTransition);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "CreateOrganization",
            async () =>
            {
                User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
                if (actor is null || !actor.IsActive)
                {
                    return Result.Failure<OrganizationResponse>(AuthErrors.Organization.UserNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);

                if (request.Type == OrganizationType.Platform)
                {
                    if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal))
                    {
                        return Result.Failure<OrganizationResponse>(AuthErrors.Organization.PlatformAdminRequired);
                    }
                }
                else
                {
                    if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsCreate, StringComparer.Ordinal))
                    {
                        return Result.Failure<OrganizationResponse>(AuthErrors.Organization.CreationNotAllowed);
                    }
                }

                // Service quyết định kiểm tra trùng slug
                if (await organizationRepository.SlugExistsAsync(trimmedSlug, cancellationToken))
                {
                    return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SlugAlreadyExists);
                }

                Role? ownerRole = await roleRepository.GetByNameAsync("Owner", cancellationToken)
                    ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);

                if (ownerRole is null)
                {
                    return Result.Failure<OrganizationResponse>(AuthErrors.Organization.RoleNotFound);
                }

                DateTime now = DateTime.UtcNow;
                Organization organization = new()
                {
                    Id = Guid.NewGuid(),
                    Name = trimmedName,
                    Slug = trimmedSlug,
                    Type = request.Type,
                    Status = OrganizationStatus.Active,
                    CreatedAtUtc = now
                };

                OrganizationMembership initialMembership = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = actorUserId,
                    OrganizationId = organization.Id,
                    RoleId = ownerRole.Id,
                    Status = MembershipStatus.Active,
                    JoinedAtUtc = now
                };

                await organizationRepository.CreateAsync(organization, initialMembership, cancellationToken);
                return Result.Success(organization.ToResponse());
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

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

        return await ApplicationExecutor.ExecuteAsync(
            "GetOrganizationById",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure<OrganizationResponse>(InactiveError(org));
                }

                if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal))
                {
                    OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                    if (membership is null || membership.Status != MembershipStatus.Active)
                    {
                        return Result.Failure<OrganizationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
                    }
                }

                return Result.Success(org.ToResponse());
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<PagedResult<OrganizationResponse>>> ListAsync(
        Guid actorUserId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Result.Failure<PagedResult<OrganizationResponse>>(AuthErrors.Paging.InvalidPage);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationResponse>>(validation.Error);
        }

        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<PagedResult<OrganizationResponse>>(AuthErrors.Organization.UserNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "ListOrganizations",
            async () =>
            {
                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);

                PagedResult<Organization> paged;
                if (globalPerms.Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal))
                {
                    paged = await organizationRepository.GetAllAsync(request, cancellationToken);
                }
                else
                {
                    paged = await organizationRepository.GetUserOrganizationsAsync(actorUserId, request, cancellationToken);
                }

                var items = paged.Items.Select(org => org.ToResponse()).ToList();
                return Result.Success(new PagedResult<OrganizationResponse>(items, paged.Page, paged.PageSize, paged.TotalCount));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
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

        return await ApplicationExecutor.ExecuteAsync(
            "UpdateOrganization",
            async () =>
            {
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

                if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal))
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

                if (!nameChanged && !activeChanged)
                {
                    return Result.Failure(AuthErrors.Organization.NoOpPatch);
                }

                OrganizationStatus? newStatus = null;
                if (activeChanged)
                {
                    newStatus = (request.IsActive!.Value, org.Status) switch
                    {
                        (true, OrganizationStatus.Pending or OrganizationStatus.Suspended) => OrganizationStatus.Active,
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
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

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
