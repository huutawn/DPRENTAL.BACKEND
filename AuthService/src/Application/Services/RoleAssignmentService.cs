using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Application.Services;

/// <summary>
/// Gán/thu hồi role trên user — cả role toàn cục (bảng UserRole) lẫn role theo Organization
/// (đổi <see cref="Domain.Entities.OrganizationMembership.RoleId"/> của một member đang active).
/// Chặn actor tự gán/tự thu hồi role của chính mình, yêu cầu actor có permission tương ứng
/// (global hoặc scoped theo đúng organization — luôn truy vấn DB tại thời điểm gọi, không tin JWT),
/// và chặn actor thao tác trên role mang permission mà actor không có — tránh tự nâng quyền.
/// Thu hồi role theo Organization KHÔNG hỗ trợ qua đây — dùng DELETE /organizations/{id}/members/{memberId}.
/// </summary>
public sealed class RoleAssignmentService(
    IRoleRepository roleRepository,
    IOrganizationMembershipRepository membershipRepository) : IRoleAssignmentService
{
    public async Task<Result> AssignAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            // Revoke role theo Organization không có nghĩa rõ ràng: mỗi membership luôn phải có
            // đúng 1 role (không giống UserRole toàn cục là nhiều-role tuỳ ý), nên "thu hồi" thực chất
            // là xoá membership — việc đó thuộc về Organizations API (đã có LastOwner protection sẵn).
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

        IReadOnlyList<string> actorGlobalPermissions = await membershipRepository.GetGlobalPermissionsAsync(actorUserId, cancellationToken);
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

            actorEffectivePermissions = await membershipRepository.GetEffectivePermissionsAsync(actorUserId, organizationId, cancellationToken);
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

}
