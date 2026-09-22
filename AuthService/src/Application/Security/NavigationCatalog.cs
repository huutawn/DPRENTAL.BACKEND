using Dprental.AuthService.Domain.DTOs.Navigation;
using Dprental.AuthService.Domain.Enums;

namespace Dprental.AuthService.Application.Security;

/// <summary>Catalog navigation authoritative của AuthService. Các permission là canonical identity.* code.</summary>
internal static class NavigationCatalog
{
    private static readonly CatalogItem[] RootItems =
    [
        new(
            "identity",
            "Identity",
            null,
            "shield",
            10,
            [],
            [
                new("identity.users", "Users", "/users", "users", 10,
                    [PermissionCode.IdentityUsersRead, PermissionCode.IdentityUsersManage]),
                new("identity.organizations", "Organizations", "/organizations", "building-2", 20,
                    [PermissionCode.IdentityOrganizationsRead, PermissionCode.IdentityOrganizationsCreate,
                     PermissionCode.IdentityOrganizationsUpdate, PermissionCode.IdentityOrganizationsSuspend]),
                new("identity.members", "Members", "/organizations/{organizationId}/members", "users-round", 30,
                    [PermissionCode.IdentityMembersRead, PermissionCode.IdentityMembersManage]),
                new("identity.roles", "Roles", "/roles", "shield-check", 40,
                    [PermissionCode.IdentityRolesRead, PermissionCode.IdentityRolesAssignGlobal,
                     PermissionCode.IdentityRolesRevokeGlobal, PermissionCode.IdentityRolesAssignOrganization,
                     PermissionCode.IdentityRolesRevokeOrganization]),
                new("identity.service-principals", "Service Principals", "/service-principals", "bot", 50,
                    [PermissionCode.IdentityServicePrincipalsRead, PermissionCode.IdentityServicePrincipalsManage,
                     PermissionCode.IdentityServicePrincipalsRotateSecret, PermissionCode.IdentityServicePrincipalsManageScopes])
            ])
    ];

    internal static IReadOnlyCollection<NavigationItemResponse> Resolve(IReadOnlySet<string> effectivePermissions) =>
        RootItems
            .Select(item => Filter(item, effectivePermissions))
            .Where(item => item is not null)
            .Cast<NavigationItemResponse>()
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

    private static NavigationItemResponse? Filter(CatalogItem item, IReadOnlySet<string> effectivePermissions)
    {
        NavigationItemResponse[] children = item.Children
            .Select(child => Filter(child, effectivePermissions))
            .Where(child => child is not null)
            .Cast<NavigationItemResponse>()
            .OrderBy(child => child.Order)
            .ThenBy(child => child.Key, StringComparer.Ordinal)
            .ToArray();

        if (item.Children.Count > 0)
        {
            return children.Length == 0
                ? null
                : new NavigationItemResponse(item.Key, item.Label, item.Href, item.Icon, item.Order, children);
        }

        return item.AnyPermissions.Any(effectivePermissions.Contains)
            ? new NavigationItemResponse(item.Key, item.Label, item.Href, item.Icon, item.Order, [])
            : null;
    }

    private sealed record CatalogItem(
        string Key,
        string Label,
        string? Href,
        string Icon,
        int Order,
        IReadOnlyCollection<string> AnyPermissions,
        IReadOnlyCollection<CatalogItem> Children)
    {
        internal CatalogItem(string key, string label, string? href, string icon, int order, IReadOnlyCollection<string> anyPermissions)
            : this(key, label, href, icon, order, anyPermissions, [])
        {
        }
    }
}
