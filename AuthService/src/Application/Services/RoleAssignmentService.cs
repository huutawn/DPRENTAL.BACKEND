using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Execution;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Gán/thu hồi role trên user — cả role toàn cục (bảng UserRole) lẫn role theo Organization.
/// </summary>
public sealed class RoleAssignmentService(
    IRoleRepository roleRepository,
    IOrganizationMembershipRepository membershipRepository,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<RoleAssignmentService> logger) : IRoleAssignmentService
{
    public async Task<Result> AssignAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out Error? actorError))
        {
            return Result.Failure(actorError);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        if (!DtoValidator.TryValidateId(request.TargetUserId, nameof(request.TargetUserId), out Error? targetError))
        {
            return Result.Failure(targetError);
        }

        if (!DtoValidator.TryValidateId(request.RoleId, nameof(request.RoleId), out Error? roleError))
        {
            return Result.Failure(roleError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "AssignRole",
            async () =>
            {
                Result guard = await GuardAsync(actorUserId, request, isAssign: true, cancellationToken);
                if (guard.IsFailure)
                {
                    return guard;
                }

                if (request.OrganizationId is Guid organizationId)
                {
                    OrganizationMembership? membership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, request.TargetUserId, cancellationToken);
                    if (membership is null)
                    {
                        return Result.Failure(AuthErrors.Organization.MembershipNotFound);
                    }

                    if (membership.RoleId == request.RoleId)
                    {
                        return Result.Failure(AuthErrors.Role.AlreadyAssigned);
                    }

                    membership.RoleId = request.RoleId;
                    bool orgUpdated = await membershipRepository.UpdateAsync(membership, cancellationToken);
                    return orgUpdated ? Result.Success() : Result.Failure(AuthErrors.Role.AssignmentFailed);
                }

                if (await roleRepository.IsAssignedAsync(request.TargetUserId, request.RoleId, cancellationToken))
                {
                    return Result.Failure(AuthErrors.Role.AlreadyAssigned);
                }

                bool assigned = await roleRepository.AssignAsync(request.TargetUserId, request.RoleId, cancellationToken);
                return assigned ? Result.Success() : Result.Failure(AuthErrors.Role.AssignmentFailed);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    public async Task<Result> RevokeAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        if (!DtoValidator.TryValidateId(actorUserId, nameof(actorUserId), out Error? actorError))
        {
            return Result.Failure(actorError);
        }

        if (!DtoValidator.TryValidate(request, out Error? validationError))
        {
            return Result.Failure(validationError);
        }

        if (!DtoValidator.TryValidateId(request.TargetUserId, nameof(request.TargetUserId), out Error? targetError))
        {
            return Result.Failure(targetError);
        }

        if (!DtoValidator.TryValidateId(request.RoleId, nameof(request.RoleId), out Error? roleError))
        {
            return Result.Failure(roleError);
        }

        return await ApplicationExecutor.ExecuteAsync(
            "RevokeRole",
            async () =>
            {
                Result guard = await GuardAsync(actorUserId, request, isAssign: false, cancellationToken);
                if (guard.IsFailure)
                {
                    return guard;
                }

                if (!await roleRepository.IsAssignedAsync(request.TargetUserId, request.RoleId, cancellationToken))
                {
                    return Result.Failure(AuthErrors.Role.NotAssigned);
                }

                bool revoked = await roleRepository.RevokeAsync(request.TargetUserId, request.RoleId, cancellationToken);
                return revoked ? Result.Success() : Result.Failure(AuthErrors.Role.NotAssigned);
            },
            logger,
            correlationIdProvider,
            cancellationToken);
    }

    private async Task<Result> GuardAsync(Guid actorUserId, AssignRoleRequest request, bool isAssign, CancellationToken cancellationToken)
    {
        if (actorUserId == request.TargetUserId)
        {
            return Result.Failure(AuthErrors.Role.CannotSelfAssign);
        }

        Role? role = await roleRepository.GetByIdAsync(request.RoleId, cancellationToken);
        if (role is null)
        {
            return Result.Failure(AuthErrors.Role.RoleNotFound);
        }

        if (role.Scope == RoleScope.Global)
        {
            if (request.OrganizationId is not null)
            {
                return Result.Failure(AuthErrors.Role.InvalidScope);
            }

            IReadOnlyList<string> actorPermissions = await roleRepository.GetUserPermissionCodesAsync(actorUserId, cancellationToken);
            string requiredPermission = isAssign
                ? PermissionCode.IdentityRolesAssignGlobal
                : PermissionCode.IdentityRolesRevokeGlobal;

            if (!actorPermissions.Contains(requiredPermission, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Role.InsufficientPermission);
            }

            IReadOnlyList<string> rolePermissions = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
            return rolePermissions.Except(actorPermissions, StringComparer.Ordinal).Any()
                ? Result.Failure(AuthErrors.Role.InsufficientPermission)
                : Result.Success();
        }

        // role.Scope == RoleScope.Organization từ đây.
        if (!isAssign)
        {
            return request.OrganizationId is not null
                ? Result.Failure(AuthErrors.Role.OrganizationRevokeNotSupported)
                : Result.Failure(AuthErrors.Role.InvalidScope);
        }

        if (request.OrganizationId is not Guid organizationId)
        {
            return Result.Failure(AuthErrors.Role.OrganizationRequired);
        }

        OrganizationMembership? targetMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, request.TargetUserId, cancellationToken);
        if (targetMembership is null || targetMembership.Status != MembershipStatus.Active)
        {
            return Result.Failure(AuthErrors.Organization.MembershipNotFound);
        }

        IReadOnlyList<string> actorGlobalPermissions = await GetGlobalPermissionsAsync(actorUserId, cancellationToken);
        bool hasGlobalOverride = actorGlobalPermissions.Contains(PermissionCode.IdentityRolesAssignOrganization, StringComparer.Ordinal)
            || actorGlobalPermissions.Contains(PermissionCode.IdentityRolesAssignGlobal, StringComparer.Ordinal);

        IReadOnlyList<string> actorEffectivePermissions = actorGlobalPermissions;
        if (!hasGlobalOverride)
        {
            OrganizationMembership? actorMembership = await membershipRepository.GetByOrganizationAndUserAsync(organizationId, actorUserId, cancellationToken);
            if (actorMembership is null || actorMembership.Status != MembershipStatus.Active)
            {
                return Result.Failure(AuthErrors.Organization.CrossOrganizationAccessDenied);
            }

            actorEffectivePermissions = await GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
            if (!actorEffectivePermissions.Contains(PermissionCode.IdentityRolesAssignOrganization, StringComparer.Ordinal))
            {
                return Result.Failure(AuthErrors.Organization.Forbidden);
            }
        }

        IReadOnlyList<string> targetRolePermissions = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
        return targetRolePermissions.Except(actorEffectivePermissions, StringComparer.Ordinal).Any()
            ? Result.Failure(AuthErrors.Organization.PrivilegeEscalation)
            : Result.Success();
    }

<<<<<<< HEAD
=======
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
>>>>>>> b8a22d3c2f59a49031461f3f63c44a44371afe2d
}
