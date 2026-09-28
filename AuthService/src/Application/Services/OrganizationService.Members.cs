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

public sealed partial class OrganizationService
{
    public async Task<Result<PagedResult<OrganizationMemberResponse>>> GetMembersAsync(
        Guid actorUserId,
        Guid organizationId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Paging.InvalidPage);
        }

        Result validation = request.Validate();
        if (validation.IsFailure)
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(validation.Error);
        }

        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.OrganizationNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "GetOrganizationMembers",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure<PagedResult<OrganizationMemberResponse>>(InactiveError(org));
                }

                if (!globalPerms.Contains(PermissionCode.IdentityMembersRead, StringComparer.Ordinal))
                {
                    OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                    if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
                    {
                        return Result.Failure<PagedResult<OrganizationMemberResponse>>(AuthErrors.Organization.CrossOrganizationAccessDenied);
                    }
                }

                PagedResult<OrganizationMembership> paged = await membershipRepository.GetMembersAsync(organizationId, request, cancellationToken);
                var items = paged.Items.Select(m => m.ToResponse()).ToList();
                return Result.Success(new PagedResult<OrganizationMemberResponse>(items, paged.Page, paged.PageSize, paged.TotalCount));
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result<OrganizationMemberResponse>> AddMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        AddOrganizationMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _))
        {
            return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.OrganizationNotFound);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure<OrganizationMemberResponse>(validationError);
        }

        if (!DtoValidator.TryValidateId(request.UserId, nameof(request.UserId), out Error? userError))
        {
            return Result.Failure<OrganizationMemberResponse>(userError);
        }

        if (!DtoValidator.TryValidateId(request.RoleId, nameof(request.RoleId), out Error? roleError))
        {
            return Result.Failure<OrganizationMemberResponse>(roleError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "AddOrganizationMember",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure<OrganizationMemberResponse>(InactiveError(org));
                }

                if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
                {
                    OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                    if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
                    {
                        return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.CrossOrganizationAccessDenied);
                    }

                    IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
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

                Role? role = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
                if (role is null)
                {
                    return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.RoleNotFound);
                }

                if (role.Scope != RoleScope.Organization)
                {
                    return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.InvalidRoleScope);
                }

                if (!globalPerms.Contains(PermissionCode.IdentityRolesAssignOrganization, StringComparer.Ordinal)
                    && !globalPerms.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal))
                {
                    IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
                    IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
                    if (targetRolePerms.Except(actorEffectivePerms, StringComparer.Ordinal).Any())
                    {
                        return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.PrivilegeEscalation);
                    }
                }

                // Service quyết định kiểm tra thành viên đã tồn tại chưa
                if (await membershipRepository.MembershipExistsAsync(organizationId, request.UserId, cancellationToken))
                {
                    return Result.Failure<OrganizationMemberResponse>(AuthErrors.Organization.MembershipAlreadyExists);
                }

                OrganizationMembership membership = new()
                {
                    Id = Guid.NewGuid(),
                    UserId = request.UserId,
                    OrganizationId = organizationId,
                    RoleId = request.RoleId,
                    Status = MembershipStatus.Invited,
                    JoinedAtUtc = DateTime.UtcNow
                };

                await membershipRepository.AddMemberAsync(membership, cancellationToken);

                membership.User = targetUser;
                membership.Role = role;
                return Result.Success(membership.ToResponse());
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> PatchMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid memberId,
        PatchOrganizationMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _) ||
            !DtoValidator.TryValidateId(memberId, nameof(memberId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        if (request is null || (request.RoleId is null && request.Status is null))
        {
            return Result.Failure(AuthErrors.Organization.EmptyPatch);
        }

        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
        {
            return Result.Failure(AuthErrors.Organization.InvalidStateTransition);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "PatchOrganizationMember",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure(InactiveError(org));
                }

                if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
                {
                    OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                    if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
                    {
                        return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
                    }

                    IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
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

                IExecutionTransaction? ownerTransaction = null;
                if (isCurrentlyOwner)
                {
                    ownerTransaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
                }

                try
                {
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
                                await organizationRepository.LockOrganizationsAsync([organizationId], cancellationToken);
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
                            IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
                            IReadOnlyList<string> targetRolePerms = await roleRepository.GetPermissionCodesAsync(request.RoleId.Value, cancellationToken);
                            if (targetRolePerms.Except(actorEffectivePerms, StringComparer.Ordinal).Any())
                            {
                                return Result.Failure(AuthErrors.Organization.PrivilegeEscalation);
                            }
                        }

                        if (isCurrentlyOwner && !IsOwner(newRole))
                        {
                            // Khóa organization theo thứ tự cố định trước khi kiểm tra Owner cuối cùng
                            await organizationRepository.LockOrganizationsAsync([organizationId], cancellationToken);
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
                    }

                    if (statusChanged)
                    {
                        member.Status = request.Status!.Value;
                    }

                    await membershipRepository.UpdateAsync(member, cancellationToken);
                    if (ownerTransaction is not null)
                    {
                        await ownerTransaction.CommitAsync(cancellationToken);
                    }
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

    public async Task<Result> DeleteMemberAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(organizationId, nameof(organizationId), out _) ||
            !DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out _) ||
            !DtoValidator.TryValidateId(memberId, nameof(memberId), out _))
        {
            return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "DeleteOrganizationMember",
            async () =>
            {
                Organization? org = await organizationRepository.GetByIdAsync(organizationId, cancellationToken);
                if (org is null)
                {
                    return Result.Failure(AuthErrors.Organization.OrganizationNotFound);
                }

                IReadOnlyList<string> globalPerms = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
                bool canManageSuspended = globalPerms.Contains(PermissionCode.IdentityOrganizationsSuspend, StringComparer.Ordinal);

                if (org.Status != OrganizationStatus.Active && !canManageSuspended)
                {
                    return Result.Failure(InactiveError(org));
                }

                if (!globalPerms.Contains(PermissionCode.IdentityMembersManage, StringComparer.Ordinal))
                {
                    OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
                    if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
                    {
                        return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
                    }

                    IReadOnlyList<string> actorEffectivePerms = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
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
                    await using var transaction = await organizationRepository.BeginTransactionAsync(cancellationToken);
                    await organizationRepository.LockOrganizationsAsync([organizationId], cancellationToken);
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

                    await membershipRepository.DeleteAsync(member.Id, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Result.Success();
                }

                await membershipRepository.DeleteAsync(member.Id, cancellationToken);
                return Result.Success();
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

}
