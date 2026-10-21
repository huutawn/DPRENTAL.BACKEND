using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.Security;

/// <summary>Quy tắc duy nhất để suy ra role, permission và organization của user, dùng chung cho access token, session principal và các API người dùng.</summary>
public static class UserAccessResolver
{
    /// <summary>Role toàn cục của user (không gồm role trong phạm vi organization).</summary>
    public static string[] Roles(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return (user.UserRoles ?? [])
            .Where(userRole => userRole.Role is not null)
            .Select(userRole => userRole.Role.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Membership đang Active — chỉ những membership này đóng góp permission và organization khi cả membership và organization đều Active.</summary>
    public static OrganizationMembership[] ActiveMemberships(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return (user.OrganizationMemberships ?? [])
            .Where(membership => membership.Status == MembershipStatus.Active
                && membership.Organization is { Status: OrganizationStatus.Active })
            .ToArray();
    }

    /// <summary>
    /// Role trong phạm vi organization (vd Owner) của các membership đang có hiệu lực. Organization chờ duyệt,
    /// bị từ chối hay bị khoá không tính — cùng điều kiện với permission.
    /// </summary>
    public static string[] OrganizationRoles(User user) =>
        ActiveMemberships(user)
            .Where(membership => membership.Role is not null)
            .Select(membership => membership.Role.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Role đưa vào access token (claim <c>r</c>) và GET /me: role toàn cục cộng dồn với role organization đang có hiệu lực.
    /// Customer sở hữu tổ chức Active nhận đồng thời cả customer và Owner (cộng dồn roles thay vì loại bỏ role customer).
    /// </summary>
    public static string[] EffectiveRoles(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Roles(user)
            .Concat(OrganizationRoles(user))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Permission từ role toàn cục cộng permission từ role của các membership Active.</summary>
    public static string[] Permissions(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return (user.UserRoles ?? [])
            .Where(userRole => userRole.Role?.RolePermissions is not null)
            .SelectMany(userRole => userRole.Role.RolePermissions)
            .Where(rolePermission => rolePermission.Permission is not null)
            .Select(rolePermission => rolePermission.Permission.Code)
            .Concat(ActiveMemberships(user)
                .Where(membership => membership.Role?.RolePermissions is not null)
                .SelectMany(membership => membership.Role.RolePermissions)
                .Where(rolePermission => rolePermission.Permission is not null)
                .Select(rolePermission => rolePermission.Permission.Code))
            .Where(code => !string.IsNullOrEmpty(code))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Danh sách ID các organization của các membership đang Active (cả membership và organization đều Active).</summary>
    public static Guid[] OrganizationIds(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return ActiveMemberships(user)
            .Select(membership => membership.OrganizationId)
            .Distinct()
            .ToArray();
    }
}
