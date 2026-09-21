using Dprental.AuthService.Domain.Entities;

using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Gán/thu hồi role trên user. Chặn actor tự gán/tự thu hồi role của chính mình, và chặn actor
/// thao tác trên role mang permission mà actor không có — tránh tự nâng quyền qua endpoint này.
/// </summary>
public sealed class RoleAssignmentService(IRoleRepository roleRepository) : IRoleAssignmentService
{
    public async Task<Result> AssignAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result guard = await GuardAsync(actorUserId, request, cancellationToken);
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

        Result guard = await GuardAsync(actorUserId, request, cancellationToken);
        if (guard.IsFailure)
        {
            return guard;
        }

        bool revoked = await roleRepository.RevokeAsync(request.TargetUserId, request.RoleId, cancellationToken);
        return revoked ? Result.Success() : Result.Failure(AuthErrors.Role.NotAssigned);
    }

    private async Task<Result> GuardAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken)
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

        IReadOnlyList<string> rolePermissions = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
        IReadOnlyList<string> actorPermissions = await roleRepository.GetUserPermissionCodesAsync(actorUserId, cancellationToken);
        if (rolePermissions.Except(actorPermissions).Any())
        {
            return Result.Failure(AuthErrors.Role.InsufficientPermission);
        }

        return Result.Success();
    }
}
