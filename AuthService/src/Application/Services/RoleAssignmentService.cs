using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Enums;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Gán/thu hồi role trên user. Chặn actor tự gán/tự thu hồi role của chính mình, yêu cầu actor
/// có permission toàn cục tương ứng (identity.roles.assign.global / identity.roles.revoke.global),
/// và chặn actor thao tác trên role mang permission mà actor không có — tránh tự nâng quyền.
/// </summary>
public sealed class RoleAssignmentService(IRoleRepository roleRepository) : IRoleAssignmentService
{
    public async Task<Result> AssignAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result guard = await GuardAsync(actorUserId, request, isAssign: true, cancellationToken);
        if (guard.IsFailure)
        {
            return guard;
        }

        if (await roleRepository.IsAssignedAsync(request.TargetUserId, request.RoleId, cancellationToken))
        {
            return Result.Failure(AuthErrors.Role.AlreadyAssigned);
        }

        bool assigned = await roleRepository.AssignAsync(request.TargetUserId, request.RoleId, cancellationToken);
        return assigned ? Result.Success() : Result.Failure(AuthErrors.Role.AssignmentFailed);
    }

    public async Task<Result> RevokeAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result guard = await GuardAsync(actorUserId, request, isAssign: false, cancellationToken);
        if (guard.IsFailure)
        {
            return guard;
        }

        bool revoked = await roleRepository.RevokeAsync(request.TargetUserId, request.RoleId, cancellationToken);
        return revoked ? Result.Success() : Result.Failure(AuthErrors.Role.NotAssigned);
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

        if (role.Scope != RoleScope.Global)
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
        if (rolePermissions.Except(actorPermissions, StringComparer.Ordinal).Any())
        {
            return Result.Failure(AuthErrors.Role.InsufficientPermission);
        }

        return Result.Success();
    }
}
