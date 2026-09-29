using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Application.Validation;
using Dprental.AuthService.Common;
using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Gán/thu hồi role toàn cục (bảng UserRole). Role phạm vi Organization bị từ chối: MVP chỉ có Owner,
/// nếu cho đổi ở đây thì có thể hạ Owner duy nhất và để tổ chức không còn ai quản lý.
/// </summary>
public sealed class RoleAssignmentService(
    IRoleRepository roleRepository) : IRoleAssignmentService
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

        // Không cho gán role mang permission mà chính actor không có — tránh tự nâng quyền gián tiếp.
        IReadOnlyList<string> rolePermissions = await roleRepository.GetPermissionCodesAsync(request.RoleId, cancellationToken);
        return rolePermissions.Except(actorPermissions, StringComparer.Ordinal).Any()
            ? Result.Failure(AuthErrors.Role.InsufficientPermission)
            : Result.Success();
    }
}
