using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.Utils;

/// <summary>Quy tắc duy nhất để suy ra role, permission và organization của user, dùng chung cho access token và GET /me.</summary>
public static class UserAccessResolver
{
    /// <summary>Role toàn cục của user (không gồm role trong phạm vi organization).</summary>
    public static string[] Roles(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.UserRoles
            .Select(userRole => userRole.Role.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Membership đang Active — chỉ những membership này đóng góp permission và organization khi cả membership và organization đều Active.</summary>
    public static OrganizationMembership[] ActiveMemberships(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.OrganizationMemberships
            .Where(membership => membership.Status == MembershipStatus.Active
                && membership.Organization is { Status: OrganizationStatus.Active })
            .ToArray();
    }

    /// <summary>Permission từ role toàn cục cộng permission từ role của các membership Active.</summary>
    public static string[] Permissions(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.UserRoles
            .SelectMany(userRole => userRole.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code))
            .Concat(ActiveMemberships(user)
                .SelectMany(membership => membership.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
