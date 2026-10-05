using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Application.Security;
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

    /// <summary>
    /// Role trong phạm vi organization (vd Owner) của các membership đang có hiệu lực. Organization chờ duyệt,
    /// bị từ chối hay bị khoá không tính — cùng điều kiện với permission.
    /// </summary>
    public static string[] OrganizationRoles(User user) =>
        ActiveMemberships(user)
            .Select(membership => membership.Role.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Role đưa vào access token (claim <c>r</c>) và GET /me: role toàn cục cộng role organization đang có hiệu lực.
    /// Khi có role organization thì role customer bị thay thế — chủ tổ chức đã duyệt chỉ còn Owner. Organization bị
    /// khoá thì role customer tự quay lại vì dòng UserRoles không bị xoá.
    /// </summary>
    public static string[] EffectiveRoles(User user)
    {
        string[] organizationRoles = OrganizationRoles(user);
        IEnumerable<string> globalRoles = Roles(user);
        if (organizationRoles.Length > 0)
        {
            globalRoles = globalRoles.Where(role => role != SystemRoleCode.Customer);
        }

        return globalRoles
            .Concat(organizationRoles)
            .Distinct(StringComparer.Ordinal)
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
