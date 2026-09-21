namespace Dprental.AuthService.Domain.Enums;

/// <summary>Catalog duy nhất ánh xạ claim ngắn gọn và alias cho role/permission trong JWT.</summary>
public static class JwtClaimCatalog
{
    public static class Claims
    {
        /// <summary>Rút gọn từ "role".</summary>
        public const string Role = "r";

        /// <summary>Rút gọn từ "perm".</summary>
        public const string Permission = "p";

        /// <summary>Rút gọn từ "org".</summary>
        public const string Organization = "o";

        /// <summary>Rút gọn từ "scope".</summary>
        public const string Scope = "s";

        /// <summary>Rút gọn từ "token_use".</summary>
        public const string TokenUse = "u";
    }

    public static class TokenUses
    {
        public const string Service = "svc";
    }

    public static class RoleAliases
    {
        public const string Admin = "adm";
        public const string AdminPlatform = "adp";
        public const string Owner = "own";
        public const string Member = "mem";

        private static readonly Dictionary<string, string> CanonicalToAlias = new(StringComparer.Ordinal)
        {
            [SystemRoleCode.Admin] = Admin,
            [SystemRoleCode.AdminPlatform] = AdminPlatform,
            [SystemRoleCode.Owner] = Owner,
            [SystemRoleCode.Member] = Member
        };

        private static readonly Dictionary<string, string> AliasToCanonical = new(StringComparer.Ordinal)
        {
            [Admin] = SystemRoleCode.Admin,
            [AdminPlatform] = SystemRoleCode.AdminPlatform,
            [Owner] = SystemRoleCode.Owner,
            [Member] = SystemRoleCode.Member
        };

        public static string ToAlias(string canonicalRole) =>
            CanonicalToAlias.TryGetValue(canonicalRole, out string? alias) ? alias : canonicalRole;

        public static string ToCanonical(string alias) =>
            AliasToCanonical.TryGetValue(alias, out string? canonical) ? canonical : alias;
    }

    public static class PermissionAliases
    {
        public const string IdentityUsersRead = "id.u.r";
        public const string IdentityUsersManage = "id.u.m";

        public const string IdentityOrganizationsRead = "id.o.r";
        public const string IdentityOrganizationsCreate = "id.o.c";
        public const string IdentityOrganizationsUpdate = "id.o.u";
        public const string IdentityOrganizationsSuspend = "id.o.s";

        public const string IdentityMembersRead = "id.m.r";
        public const string IdentityMembersManage = "id.m.m";

        public const string IdentityRolesRead = "id.r.r";
        public const string IdentityRolesAssignGlobal = "id.r.ag";
        public const string IdentityRolesRevokeGlobal = "id.r.rg";
        public const string IdentityRolesAssignOrganization = "id.r.ao";
        public const string IdentityRolesRevokeOrganization = "id.r.ro";

        public const string IdentityServicePrincipalsRead = "id.sp.r";
        public const string IdentityServicePrincipalsManage = "id.sp.m";
        public const string IdentityServicePrincipalsRotateSecret = "id.sp.rs";
        public const string IdentityServicePrincipalsManageScopes = "id.sp.ms";

        private static readonly Dictionary<string, string> CanonicalToAlias = new(StringComparer.Ordinal)
        {
            [PermissionCode.IdentityUsersRead] = IdentityUsersRead,
            [PermissionCode.IdentityUsersManage] = IdentityUsersManage,
            [PermissionCode.IdentityOrganizationsRead] = IdentityOrganizationsRead,
            [PermissionCode.IdentityOrganizationsCreate] = IdentityOrganizationsCreate,
            [PermissionCode.IdentityOrganizationsUpdate] = IdentityOrganizationsUpdate,
            [PermissionCode.IdentityOrganizationsSuspend] = IdentityOrganizationsSuspend,
            [PermissionCode.IdentityMembersRead] = IdentityMembersRead,
            [PermissionCode.IdentityMembersManage] = IdentityMembersManage,
            [PermissionCode.IdentityRolesRead] = IdentityRolesRead,
            [PermissionCode.IdentityRolesAssignGlobal] = IdentityRolesAssignGlobal,
            [PermissionCode.IdentityRolesRevokeGlobal] = IdentityRolesRevokeGlobal,
            [PermissionCode.IdentityRolesAssignOrganization] = IdentityRolesAssignOrganization,
            [PermissionCode.IdentityRolesRevokeOrganization] = IdentityRolesRevokeOrganization,
            [PermissionCode.IdentityServicePrincipalsRead] = IdentityServicePrincipalsRead,
            [PermissionCode.IdentityServicePrincipalsManage] = IdentityServicePrincipalsManage,
            [PermissionCode.IdentityServicePrincipalsRotateSecret] = IdentityServicePrincipalsRotateSecret,
            [PermissionCode.IdentityServicePrincipalsManageScopes] = IdentityServicePrincipalsManageScopes,
        };

        private static readonly Dictionary<string, string> AliasToCanonical = new(StringComparer.Ordinal)
        {
            [IdentityUsersRead] = PermissionCode.IdentityUsersRead,
            [IdentityUsersManage] = PermissionCode.IdentityUsersManage,
            [IdentityOrganizationsRead] = PermissionCode.IdentityOrganizationsRead,
            [IdentityOrganizationsCreate] = PermissionCode.IdentityOrganizationsCreate,
            [IdentityOrganizationsUpdate] = PermissionCode.IdentityOrganizationsUpdate,
            [IdentityOrganizationsSuspend] = PermissionCode.IdentityOrganizationsSuspend,
            [IdentityMembersRead] = PermissionCode.IdentityMembersRead,
            [IdentityMembersManage] = PermissionCode.IdentityMembersManage,
            [IdentityRolesRead] = PermissionCode.IdentityRolesRead,
            [IdentityRolesAssignGlobal] = PermissionCode.IdentityRolesAssignGlobal,
            [IdentityRolesRevokeGlobal] = PermissionCode.IdentityRolesRevokeGlobal,
            [IdentityRolesAssignOrganization] = PermissionCode.IdentityRolesAssignOrganization,
            [IdentityRolesRevokeOrganization] = PermissionCode.IdentityRolesRevokeOrganization,
            [IdentityServicePrincipalsRead] = PermissionCode.IdentityServicePrincipalsRead,
            [IdentityServicePrincipalsManage] = PermissionCode.IdentityServicePrincipalsManage,
            [IdentityServicePrincipalsRotateSecret] = PermissionCode.IdentityServicePrincipalsRotateSecret,
            [IdentityServicePrincipalsManageScopes] = PermissionCode.IdentityServicePrincipalsManageScopes,
        };

        public static string ToAlias(string canonicalPermission) =>
            CanonicalToAlias.TryGetValue(canonicalPermission, out string? alias) ? alias : canonicalPermission;

        public static string ToCanonical(string alias) =>
            AliasToCanonical.TryGetValue(alias, out string? canonical) ? canonical : alias;
    }
}
