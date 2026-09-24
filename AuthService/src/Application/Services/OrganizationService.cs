using Dprental.AuthService.Common.Enums;
using System.Text.RegularExpressions;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed partial class OrganizationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    INavigationCatalogRepository? navigationCatalogRepository = null) : IOrganizationService
{
    private static readonly Guid SeededOwnerRoleId = new("a1010000-0000-0000-0000-000000000001");

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalSlugRegex();

    public async Task<Result<OrganizationResponse>> CreateAsync(
        Guid actorUserId,
        CreateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.UserNotFound);
        }

        string trimmedName = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidName);
        }

        string trimmedSlug = request.Slug?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSlug) || trimmedSlug.Length > 100 || !CanonicalSlugRegex().IsMatch(trimmedSlug))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidSlugFormat);
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InvalidStateTransition);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.UserNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);

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
            IsActive = true,
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

        try
        {
            Result<NavigationCatalog> navigationCatalogResult = await CreateCatalogFromTemplateAsync(organization, cancellationToken);
            if (navigationCatalogResult.IsFailure)
            {
                return Result.Failure<OrganizationResponse>(navigationCatalogResult.Error);
            }

            NavigationCatalog navigationCatalog = navigationCatalogResult.Value;
            Organization created = await organizationRepository.CreateAsync(organization, initialMembership, navigationCatalog, cancellationToken);
            return new OrganizationResponse(
                created.Id,
                created.Name,
                created.Slug,
                created.Type,
                created.IsActive,
                created.CreatedAtUtc);
        }
        catch (DuplicateSlugException)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.SlugAlreadyExists);
        }
    }

    private async Task<Result<NavigationCatalog>> CreateCatalogFromTemplateAsync(Organization organization, CancellationToken cancellationToken)
    {
        if (navigationCatalogRepository is null)
        {
            return Result.Failure<NavigationCatalog>(AuthErrors.Navigation.CatalogNotFound);
        }

        NavigationCatalog? template = await navigationCatalogRepository.GetTemplateAsync(organization.Type, cancellationToken);
        if (template is null)
        {
            return Result.Failure<NavigationCatalog>(AuthErrors.Navigation.CatalogNotFound);
        }

        NavigationCatalog catalog = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            DashboardType = organization.Type,
            CreatedAtUtc = DateTime.UtcNow
        };
        Dictionary<Guid, NavigationItem> copies = [];
        foreach (NavigationItem source in template.Items)
        {
            NavigationItem copy = new()
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                SourceTemplateItemId = source.Id,
                Key = source.Key,
                Label = source.Label,
                Href = source.Href,
                Icon = source.Icon,
                Order = source.Order,
                IsActive = source.IsActive
            };
            foreach (NavigationItemPermission permission in source.Permissions)
            {
                copy.Permissions.Add(new NavigationItemPermission { NavigationItemId = copy.Id, PermissionId = permission.PermissionId });
            }
            copies[source.Id] = copy;
            catalog.Items.Add(copy);
        }
        foreach (NavigationItem source in template.Items.Where(item => item.ParentId is not null))
        {
            copies[source.Id].ParentId = copies[source.ParentId!.Value].Id;
        }
        return catalog;
    }

    public async Task<Result<OrganizationResponse>> GetByIdAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure<OrganizationResponse>(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal))
            {
                return Result.Failure<OrganizationResponse>(AuthErrors.Organization.Forbidden);
            }
        }

        return new OrganizationResponse(
            org.Id,
            org.Name,
            org.Slug,
            org.Type,
            org.IsActive,
            org.CreatedAtUtc);
    }

    public async Task<Result<PagedResult<OrganizationResponse>>> ListAsync(
        Guid actorUserId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty)
        {
            return Result.Failure<PagedResult<OrganizationResponse>>(AuthErrors.Organization.Forbidden);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationResponse>>(validation.Error);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool hasGlobalRead = globalPerms.Contains(PermissionCode.IdentityOrganizationsRead, StringComparer.Ordinal);

        PagedResult<Organization> pagedOrgs = hasGlobalRead
            ? await organizationRepository.GetAllAsync(request, cancellationToken)
            : await organizationRepository.GetUserOrganizationsAsync(actorUserId, request, cancellationToken);

        OrganizationResponse[] mapped = pagedOrgs.Items
            .Select(org => new OrganizationResponse(org.Id, org.Name, org.Slug, org.Type, org.IsActive, org.CreatedAtUtc))
            .ToArray();

        return new PagedResult<OrganizationResponse>(mapped, pagedOrgs.Page, pagedOrgs.PageSize, pagedOrgs.TotalCount, pagedOrgs.TotalPages);
    }

    public async Task<Result> UpdateAsync(
        Guid actorUserId,
        Guid organizationId,
        UpdateOrganizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request.Name is null && request.IsActive is null)
        {
            return Result.Failure(AuthErrors.Organization.EmptyPatch);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (request.IsActive.HasValue && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.PlatformAdminRequired);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(PermissionCode.IdentityOrganizationsUpdate, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Organization.Forbidden);
            }
        }

        string? trimmedName = request.Name?.Trim();
        if (request.Name is not null && (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150))
        {
            return Result.Failure(AuthErrors.Organization.InvalidName);
        }

        bool nameChanged = trimmedName is not null && !string.Equals(trimmedName, org.Name, StringComparison.Ordinal);
        bool activeChanged = request.IsActive.HasValue && request.IsActive.Value != org.IsActive;

        if (!nameChanged && !activeChanged)
        {
            return Result.Failure(AuthErrors.Organization.NoOpPatch);
        }

        if (nameChanged)
        {
            org.Name = trimmedName!;
        }

        if (activeChanged)
        {
            org.IsActive = request.IsActive!.Value;
        }

        await organizationRepository.UpdateAsync(org, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PagedResult<OrganizationMemberResponse>>> GetMembersAsync(
        Guid actorUserId,
        Guid organizationId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.OrganizationNotFound);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(validation.Error);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityMembersRead, StringComparer.Ordinal))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(PermissionCode.IdentityMembersRead, StringComparer.Ordinal))
            {
                return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.Forbidden);
            }
        }

        PagedResult<OrganizationMembership> pagedMembers = await membershipRepository.GetMembersAsync(organizationId, request, cancellationToken);
        OrganizationMemberResponse[] mapped = pagedMembers.Items
            .Select(m => new OrganizationMemberResponse(
                m.Id,
                m.UserId,
                m.User?.Email ?? string.Empty,
                m.User?.FullName ?? string.Empty,
                m.RoleId,
                m.Role?.Name ?? string.Empty,
                m.Status,
                m.JoinedAtUtc))
            .ToArray();

        return new PagedResult<OrganizationMemberResponse>(mapped, pagedMembers.Page, pagedMembers.PageSize, pagedMembers.TotalCount, pagedMembers.TotalPages);
    }

    public async Task<Result<OrganizationMemberResponse>> AddMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        AddOrganizationMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request.UserId == Guid.Empty || request.RoleId == Guid.Empty)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.UserNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
            {
                return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.Forbidden);
            }
        }

        User? targetUser = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (targetUser is null)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.UserNotFound);
        }

        if (!targetUser.IsActive)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.UserInactive);
        }

        Role? targetRole = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (targetRole is null)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.RoleNotFound);
        }

        if (targetRole.Scope != RoleScope.Organization)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.InvalidRoleScope);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityRolesAssignOrganization, StringComparer.Ordinal)
            && !globalPerms.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
        {
            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
            if (targetRolePerms.Except(actorEffectivePerms, StringComparer.Ordinal).Any())
            {
                return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.PrivilegeEscalation);
            }
        }

        if (await membershipRepository.MembershipExistsAsync(organizationId, request.UserId, cancellationToken))
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.MembershipAlreadyExists);
        }

        OrganizationMembership membership = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = request.UserId,
            RoleId = request.RoleId,
            Status = MembershipStatus.Invited,
            JoinedAtUtc = DateTime.UtcNow
        };

        try
        {
            OrganizationMembership created = await membershipRepository.AddMemberAsync(membership, cancellationToken);
            return new OrganizationMemberResponse(
                created.Id,
                targetUser.Id,
                targetUser.Email,
                targetUser.FullName,
                targetRole.Id,
                targetRole.Name,
                created.Status,
                created.JoinedAtUtc);
        }
        catch (DuplicateMembershipException)
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.MembershipAlreadyExists);
        }
    }

    public async Task<Result> PatchMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid memberId,
        PatchOrganizationMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (actorUserId == Guid.Empty || organizationId == Guid.Empty || memberId == Guid.Empty)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request.RoleId is null && request.Status is null)
        {
            return Result.Failure(AuthErrors.Organization.EmptyPatch);
        }

        if (request.RoleId.HasValue && request.RoleId.Value == Guid.Empty)
        {
            return Result.Failure(AuthErrors.Organization.RoleNotFound);
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Organization.Forbidden);
            }
        }

        OrganizationMembership? member = await membershipRepository.FindMemberAsync(organizationId, memberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(AuthErrors.Organization.MembershipNotFound);
        }

        Role? currentRole = member.Role ?? await roleRepository.GetByIdAsync(member.RoleId, cancellationToken);
        bool isCurrentlyOwner = IsOwner(currentRole);

        if (request.Status.HasValue)
        {
            MembershipStatus newStatus = request.Status.Value;

            if (newStatus == MembershipStatus.Active)
            {
                if (member.Status == MembershipStatus.Invited)
                {
                    return Result.Failure(AuthErrors.Organization.CannotActivateInvitation);
                }
            }
            else if (newStatus == MembershipStatus.Suspended)
            {
                if (member.Status == MembershipStatus.Invited)
                {
                    return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
                }

                if (member.Status == MembershipStatus.Active && isCurrentlyOwner)
                {
                    int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                    if (activeOwners <= 1)
                    {
                        return Result.Failure(AuthErrors.Organization.LastOwner);
                    }
                }
            }
            else if (newStatus == MembershipStatus.Invited)
            {
                if (member.Status != MembershipStatus.Invited)
                {
                    return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
                }
            }
        }

        Role? newRole = null;
        if (request.RoleId.HasValue)
        {
            newRole = await roleRepository.GetByIdAsync(request.RoleId.Value, cancellationToken);
            if (newRole is null)
            {
                return Result.Failure(AuthErrors.Organization.RoleNotFound);
            }

            if (newRole.Scope != RoleScope.Organization)
            {
                return Result.Failure(AuthErrors.Organization.InvalidRoleScope);
            }

            if (!globalPerms.Contains(PermissionCode.IdentityRolesAssignOrganization, StringComparer.Ordinal)
                && !globalPerms.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
            {
                IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
                IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId.Value, cancellationToken);
                if (targetRolePerms.Except(actorEffectivePerms, StringComparer.Ordinal).Any())
                {
                    return Result.Failure(AuthErrors.Organization.PrivilegeEscalation);
                }
            }

            if (isCurrentlyOwner && !IsOwner(newRole))
            {
                int totalOwners = await membershipRepository.CountOwnersAsync(organizationId, cancellationToken);
                if (totalOwners <= 1)
                {
                    return Result.Failure(AuthErrors.Organization.LastOwner);
                }

                if (member.Status == MembershipStatus.Active)
                {
                    int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                    if (activeOwners <= 1)
                    {
                        return Result.Failure(AuthErrors.Organization.LastOwner);
                    }
                }
            }
        }

        bool roleChanged = request.RoleId.HasValue && request.RoleId.Value != member.RoleId;
        bool statusChanged = request.Status.HasValue && request.Status.Value != member.Status;

        if (!roleChanged && !statusChanged)
        {
            return Result.Failure(AuthErrors.Organization.NoOpPatch);
        }

        if (roleChanged)
        {
            member.RoleId = request.RoleId!.Value;
            if (newRole is not null)
            {
                member.Role = newRole;
            }
        }
        if (statusChanged)
        {
            member.Status = request.Status!.Value;
        }

        await membershipRepository.UpdateAsync(member, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeleteMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty || memberId == Guid.Empty)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.InactiveOrganization);
        }

        if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Organization.Forbidden);
            }
        }

        OrganizationMembership? member = await membershipRepository.FindMemberAsync(organizationId, memberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(AuthErrors.Organization.MembershipNotFound);
        }

        Role? currentRole = member.Role ?? await roleRepository.GetByIdAsync(member.RoleId, cancellationToken);
        if (IsOwner(currentRole))
        {
            int totalOwners = await membershipRepository.CountOwnersAsync(organizationId, cancellationToken);
            if (totalOwners <= 1)
            {
                return Result.Failure(AuthErrors.Organization.LastOwner);
            }

            if (member.Status == MembershipStatus.Active)
            {
                int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                if (activeOwners <= 1)
                {
                    return Result.Failure(AuthErrors.Organization.LastOwner);
                }
            }
        }

        await membershipRepository.DeleteAsync(member.Id, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> AcceptInvitationAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure(AuthErrors.Organization.UserNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

        if (!org.IsActive && !canManageSuspended)
        {
            return Result.Failure(AuthErrors.Organization.InactiveOrganization);
        }

        OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
        if (membership is null)
        {
            return Result.Failure(AuthErrors.Organization.MembershipNotFound);
        }

        if (membership.Status != MembershipStatus.Invited)
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        membership.Status = MembershipStatus.Active;
        membership.JoinedAtUtc = DateTime.UtcNow;

        await membershipRepository.UpdateAsync(membership, cancellationToken);
        return Result.Success();
    }

    private static bool IsOwner(Role? role) =>
        role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
}
