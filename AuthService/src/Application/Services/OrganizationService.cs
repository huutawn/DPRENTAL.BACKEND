using System.Text.RegularExpressions;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Security;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Organization;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

public sealed partial class OrganizationService(
    IOrganizationRepository organizationRepository,
    IOrganizationMembershipRepository membershipRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository) : IOrganizationService
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
            return Result.Failure<OrganizationResponse>(OrganizationErrors.UserNotFound);
        }

        string trimmedName = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.InvalidName);
        }

        string trimmedSlug = request.Slug?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedSlug) || trimmedSlug.Length > 100 || !CanonicalSlugRegex().IsMatch(trimmedSlug))
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.InvalidSlugFormat);
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.InvalidStateTransition);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.UserNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (request.Type == OrganizationType.Platform)
        {
            if (!isPlatformAdmin)
            {
                return Result.Failure<OrganizationResponse>(OrganizationErrors.PlatformAdminRequired);
            }
        }
        else
        {
            if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Create))
            {
                return Result.Failure<OrganizationResponse>(OrganizationErrors.CreationNotAllowed);
            }
        }

        if (await organizationRepository.SlugExistsAsync(trimmedSlug, cancellationToken))
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.SlugAlreadyExists);
        }

        Role? ownerRole = await roleRepository.GetByNameAsync("Owner", cancellationToken)
            ?? await roleRepository.GetByIdAsync(SeededOwnerRoleId, cancellationToken);

        if (ownerRole is null)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.RoleNotFound);
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
            Organization created = await organizationRepository.CreateAsync(organization, initialMembership, cancellationToken);
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
            return Result.Failure<OrganizationResponse>(OrganizationErrors.SlugAlreadyExists);
        }
    }

    public async Task<Result<OrganizationResponse>> GetByIdAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure<OrganizationResponse>(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Read))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure<OrganizationResponse>(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(Permissions.Organizations.Read))
            {
                return Result.Failure<OrganizationResponse>(OrganizationErrors.Forbidden);
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

    public async Task<Result<IReadOnlyList<OrganizationResponse>>> ListAsync(
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty)
        {
            return Result.Failure<IReadOnlyList<OrganizationResponse>>(OrganizationErrors.Forbidden);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        IReadOnlyList<Organization> orgs = isPlatformAdmin
            ? await organizationRepository.GetAllAsync(cancellationToken)
            : await organizationRepository.GetUserOrganizationsAsync(actorUserId, cancellationToken);

        OrganizationResponse[] mapped = orgs
            .Select(org => new OrganizationResponse(org.Id, org.Name, org.Slug, org.Type, org.IsActive, org.CreatedAtUtc))
            .ToArray();

        return mapped;
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
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        if (request.Name is null && request.IsActive is null)
        {
            return Result.Failure(OrganizationErrors.EmptyPatch);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (request.IsActive.HasValue && !isPlatformAdmin)
        {
            return Result.Failure(OrganizationErrors.PlatformAdminRequired);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Update))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(Permissions.Organizations.Update))
            {
                return Result.Failure(OrganizationErrors.Forbidden);
            }
        }

        string? trimmedName = request.Name?.Trim();
        if (request.Name is not null && (string.IsNullOrWhiteSpace(trimmedName) || trimmedName.Length > 150))
        {
            return Result.Failure(OrganizationErrors.InvalidName);
        }

        bool nameChanged = trimmedName is not null && !string.Equals(trimmedName, org.Name, StringComparison.Ordinal);
        bool activeChanged = request.IsActive.HasValue && request.IsActive.Value != org.IsActive;

        if (!nameChanged && !activeChanged)
        {
            return Result.Failure(OrganizationErrors.NoOpPatch);
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

    public async Task<Result<IReadOnlyList<OrganizationMemberResponse>>> GetMembersAsync(
        Guid actorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty || organizationId == Guid.Empty)
        {
            return Result.Failure<IReadOnlyList<OrganizationMemberResponse>>(OrganizationErrors.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<IReadOnlyList<OrganizationMemberResponse>>(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure<IReadOnlyList<OrganizationMemberResponse>>(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Members.Read))
        {
            OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (membership is null || membership.Status != MembershipStatus.Active)
            {
                return Result.Failure<IReadOnlyList<OrganizationMemberResponse>>(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> effectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!effectivePerms.Contains(Permissions.Organizations.Members.Read))
            {
                return Result.Failure<IReadOnlyList<OrganizationMemberResponse>>(OrganizationErrors.Forbidden);
            }
        }

        IReadOnlyList<OrganizationMembership> members = await membershipRepository.GetMembersAsync(organizationId, cancellationToken);
        OrganizationMemberResponse[] mapped = members
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

        return mapped;
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
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.OrganizationNotFound);
        }

        if (request.UserId == Guid.Empty || request.RoleId == Guid.Empty)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.UserNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Members.Manage))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(Permissions.Organizations.Members.Manage))
            {
                return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.Forbidden);
            }
        }

        User? targetUser = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (targetUser is null)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.UserNotFound);
        }

        if (!targetUser.IsActive)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.UserInactive);
        }

        Role? targetRole = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (targetRole is null)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.RoleNotFound);
        }

        if (targetRole.Scope != RoleScope.Organization)
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.InvalidRoleScope);
        }

        if (!isPlatformAdmin)
        {
            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
            if (targetRolePerms.Except(actorEffectivePerms).Any())
            {
                return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.PrivilegeEscalation);
            }
        }

        if (await membershipRepository.MembershipExistsAsync(organizationId, request.UserId, cancellationToken))
        {
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.MembershipAlreadyExists);
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
            return Result.Failure<OrganizationMemberResponse>(OrganizationErrors.MembershipAlreadyExists);
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
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        if (request.RoleId is null && request.Status is null)
        {
            return Result.Failure(OrganizationErrors.EmptyPatch);
        }

        if (request.RoleId.HasValue && request.RoleId.Value == Guid.Empty)
        {
            return Result.Failure(OrganizationErrors.RoleNotFound);
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            return Result.Failure(OrganizationErrors.InvalidStateTransition);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Members.Manage))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(Permissions.Organizations.Members.Manage))
            {
                return Result.Failure(OrganizationErrors.Forbidden);
            }
        }

        OrganizationMembership? member = await membershipRepository.FindMemberAsync(organizationId, memberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(OrganizationErrors.MembershipNotFound);
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
                    return Result.Failure(OrganizationErrors.CannotActivateInvitation);
                }
            }
            else if (newStatus == MembershipStatus.Suspended)
            {
                if (member.Status == MembershipStatus.Invited)
                {
                    return Result.Failure(OrganizationErrors.InvalidStateTransition);
                }

                if (member.Status == MembershipStatus.Active && isCurrentlyOwner)
                {
                    int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                    if (activeOwners <= 1)
                    {
                        return Result.Failure(OrganizationErrors.LastOwner);
                    }
                }
            }
            else if (newStatus == MembershipStatus.Invited)
            {
                if (member.Status != MembershipStatus.Invited)
                {
                    return Result.Failure(OrganizationErrors.InvalidStateTransition);
                }
            }
        }

        Role? newRole = null;
        if (request.RoleId.HasValue)
        {
            newRole = await roleRepository.GetByIdAsync(request.RoleId.Value, cancellationToken);
            if (newRole is null)
            {
                return Result.Failure(OrganizationErrors.RoleNotFound);
            }

            if (newRole.Scope != RoleScope.Organization)
            {
                return Result.Failure(OrganizationErrors.InvalidRoleScope);
            }

            if (!isPlatformAdmin)
            {
                IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
                IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId.Value, cancellationToken);
                if (targetRolePerms.Except(actorEffectivePerms).Any())
                {
                    return Result.Failure(OrganizationErrors.PrivilegeEscalation);
                }
            }

            if (isCurrentlyOwner && !IsOwner(newRole))
            {
                int totalOwners = await membershipRepository.CountOwnersAsync(organizationId, cancellationToken);
                if (totalOwners <= 1)
                {
                    return Result.Failure(OrganizationErrors.LastOwner);
                }

                if (member.Status == MembershipStatus.Active)
                {
                    int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                    if (activeOwners <= 1)
                    {
                        return Result.Failure(OrganizationErrors.LastOwner);
                    }
                }
            }
        }

        bool roleChanged = request.RoleId.HasValue && request.RoleId.Value != member.RoleId;
        bool statusChanged = request.Status.HasValue && request.Status.Value != member.Status;

        if (!roleChanged && !statusChanged)
        {
            return Result.Failure(OrganizationErrors.NoOpPatch);
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
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure(OrganizationErrors.InactiveOrganization);
        }

        if (!isPlatformAdmin && !globalPerms.Contains(Permissions.Organizations.Members.Manage))
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(OrganizationErrors.CrossOrganizationAccessDenied);
            }

            IReadOnlyList<string> actorEffectivePerms = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePerms.Contains(Permissions.Organizations.Members.Manage))
            {
                return Result.Failure(OrganizationErrors.Forbidden);
            }
        }

        OrganizationMembership? member = await membershipRepository.FindMemberAsync(organizationId, memberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(OrganizationErrors.MembershipNotFound);
        }

        Role? currentRole = member.Role ?? await roleRepository.GetByIdAsync(member.RoleId, cancellationToken);
        if (IsOwner(currentRole))
        {
            int totalOwners = await membershipRepository.CountOwnersAsync(organizationId, cancellationToken);
            if (totalOwners <= 1)
            {
                return Result.Failure(OrganizationErrors.LastOwner);
            }

            if (member.Status == MembershipStatus.Active)
            {
                int activeOwners = await membershipRepository.CountActiveOwnersAsync(organizationId, cancellationToken);
                if (activeOwners <= 1)
                {
                    return Result.Failure(OrganizationErrors.LastOwner);
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
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        User? actor = await userRepository.GetByIdAsync(actorUserId, cancellationToken);
        if (actor is null || !actor.IsActive)
        {
            return Result.Failure(OrganizationErrors.UserNotFound);
        }

        Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
        if (org is null)
        {
            return Result.Failure(OrganizationErrors.OrganizationNotFound);
        }

        IReadOnlyList<string> globalPerms = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool isPlatformAdmin = IsPlatformAdmin(globalPerms);

        if (!org.IsActive && !isPlatformAdmin)
        {
            return Result.Failure(OrganizationErrors.InactiveOrganization);
        }

        OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
        if (membership is null)
        {
            return Result.Failure(OrganizationErrors.MembershipNotFound);
        }

        if (membership.Status != MembershipStatus.Invited)
        {
            return Result.Failure(OrganizationErrors.InvalidStateTransition);
        }

        membership.Status = MembershipStatus.Active;
        membership.JoinedAtUtc = DateTime.UtcNow;

        await membershipRepository.UpdateAsync(membership, cancellationToken);
        return Result.Success();
    }

    private static bool IsPlatformAdmin(IReadOnlyList<string> globalPermissions) =>
        globalPermissions.Contains(Permissions.Platform.Manage) ||
        globalPermissions.Contains("platform-management") ||
        globalPermissions.Contains("platform.management");

    private static bool IsOwner(Role? role) =>
        role is not null && string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase);
}
