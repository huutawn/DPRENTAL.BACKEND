namespace Dprental.AuthService.Domain.Enums;

/// <summary>Mã permission chuẩn mang namespace identity.*.</summary>
public static class PermissionCode
{
    public const string IdentityUsersRead = "identity.users.read";
    public const string IdentityUsersManage = "identity.users.manage";

    public const string IdentityOrganizationsRead = "identity.organizations.read";
    public const string IdentityOrganizationsCreate = "identity.organizations.create";
    public const string IdentityOrganizationsUpdate = "identity.organizations.update";
    public const string IdentityOrganizationsSuspend = "identity.organizations.suspend";

    public const string IdentityMembersRead = "identity.members.read";
    public const string IdentityMembersManage = "identity.members.manage";

    public const string IdentityRolesRead = "identity.roles.read";
    public const string IdentityRolesAssignGlobal = "identity.roles.assign.global";
    public const string IdentityRolesRevokeGlobal = "identity.roles.revoke.global";
    public const string IdentityRolesAssignOrganization = "identity.roles.assign.organization";
    public const string IdentityRolesRevokeOrganization = "identity.roles.revoke.organization";

    public const string IdentityServicePrincipalsRead = "identity.service-principals.read";
    public const string IdentityServicePrincipalsManage = "identity.service-principals.manage";
    public const string IdentityServicePrincipalsRotateSecret = "identity.service-principals.rotate-secret";
    public const string IdentityServicePrincipalsManageScopes = "identity.service-principals.manage-scopes";

    public const string IdentityNavigationManage = "identity.navigation.manage";

    public static readonly IReadOnlyCollection<string> All =
    [
        IdentityUsersRead,
        IdentityUsersManage,
        IdentityOrganizationsRead,
        IdentityOrganizationsCreate,
        IdentityOrganizationsUpdate,
        IdentityOrganizationsSuspend,
        IdentityMembersRead,
        IdentityMembersManage,
        IdentityRolesRead,
        IdentityRolesAssignGlobal,
        IdentityRolesRevokeGlobal,
        IdentityRolesAssignOrganization,
        IdentityRolesRevokeOrganization,
        IdentityServicePrincipalsRead,
        IdentityServicePrincipalsManage,
        IdentityServicePrincipalsRotateSecret,
        IdentityServicePrincipalsManageScopes,
        IdentityNavigationManage
    ];
}
