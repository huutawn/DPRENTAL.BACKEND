using System.Reflection;
using AuthService.Domain.Migrations;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class RbacMigrationTests
{
    private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
    private static readonly Guid CustomerRoleId = new("82127c20-a094-4d8b-a1c7-b94266c001d2");
    private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
    private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");
    private static readonly Guid AdminPlatformRoleId = new("a1010000-0000-0000-0000-000000000003");

    [Fact]
    public void MigrationUpContainsAllRequiredOperations()
    {
        StandardizeIdentityRbac migration = new();
        MigrationBuilder builder = new("Npgsql");

        MethodInfo? upMethod = typeof(StandardizeIdentityRbac).GetMethod(
            "Up",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(upMethod);
        upMethod.Invoke(migration, [builder]);

        IReadOnlyList<MigrationOperation> ops = builder.Operations;
        Assert.NotEmpty(ops);

        // 1. UserRoles cleanup for customer role
        Assert.Contains(ops.OfType<SqlOperation>(), sql =>
            sql.Sql.Contains("DELETE FROM auth.\"UserRoles\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Sql.Contains(CustomerRoleId.ToString(), StringComparison.OrdinalIgnoreCase));

        // 2. Customer role deletion
        Assert.Contains(ops.OfType<DeleteDataOperation>(), del =>
            del.Table == "Roles" &&
            del.KeyValues != null &&
            del.KeyValues.Cast<object>().Any(v => v.Equals(CustomerRoleId)));

        // 3. Platform.manage deletion
        Guid platformManagePermId = new("b2020000-0000-0000-0000-000000000006");
        Assert.Contains(ops.OfType<DeleteDataOperation>(), del =>
            del.Table == "Permissions" &&
            del.KeyValues != null &&
            del.KeyValues.Cast<object>().Any(v => v.Equals(platformManagePermId)));
        Assert.Contains(ops.OfType<DeleteDataOperation>(), del =>
            del.Table == "RolePermissions" &&
            del.KeyValues != null &&
            del.KeyValues.Cast<object>().Any(v => v.Equals(platformManagePermId)));

        // 4. UpdateData operations to identity.*
        List<UpdateDataOperation> updates = ops.OfType<UpdateDataOperation>().Where(u => u.Table == "Permissions").ToList();
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityRolesAssignGlobal)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityRolesRevokeGlobal)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityOrganizationsCreate)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityOrganizationsRead)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityOrganizationsUpdate)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityMembersRead)));
        Assert.Contains(updates, u => u.Values.Cast<object>().Any(v => v.Equals(PermissionCode.IdentityMembersManage)));

        // 5. AdminPlatform role inserted
        List<InsertDataOperation> roleInserts = ops.OfType<InsertDataOperation>().Where(i => i.Table == "Roles").ToList();
        Assert.Contains(roleInserts, r =>
            r.Values != null &&
            EnumerateValues(r.Values).Any(v => v.Equals("admin_platform")));

        // 6. New permissions inserted
        List<InsertDataOperation> permInserts = ops.OfType<InsertDataOperation>().Where(i => i.Table == "Permissions").ToList();
        List<object> insertedPermValues = permInserts.SelectMany(p => EnumerateValues(p.Values)).ToList();
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityUsersRead));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityUsersManage));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityOrganizationsSuspend));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityRolesRead));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityRolesAssignOrganization));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityRolesRevokeOrganization));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityServicePrincipalsRead));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityServicePrincipalsManage));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityServicePrincipalsRotateSecret));
        Assert.Contains(insertedPermValues, v => v.Equals(PermissionCode.IdentityServicePrincipalsManageScopes));
    }

    [Fact]
    public void StandardizedRbacContainsExpectedRolePermissionsAndNoLegacyPermissions()
    {
        // Simulate cumulative state of permissions and roles after all migrations
        Dictionary<Guid, string> permissions = new()
        {
            [new Guid("f6889773-6650-4ba5-964a-97f87ab921ce")] = PermissionCode.IdentityRolesAssignGlobal,
            [new Guid("e1ff306f-1f55-498b-99ea-afa87d445a42")] = PermissionCode.IdentityRolesRevokeGlobal,
            [new Guid("b2020000-0000-0000-0000-000000000001")] = PermissionCode.IdentityOrganizationsCreate,
            [new Guid("b2020000-0000-0000-0000-000000000002")] = PermissionCode.IdentityOrganizationsRead,
            [new Guid("b2020000-0000-0000-0000-000000000003")] = PermissionCode.IdentityOrganizationsUpdate,
            [new Guid("b2020000-0000-0000-0000-000000000004")] = PermissionCode.IdentityMembersRead,
            [new Guid("b2020000-0000-0000-0000-000000000005")] = PermissionCode.IdentityMembersManage,
            [new Guid("c3030000-0000-0000-0000-000000000001")] = PermissionCode.IdentityUsersRead,
            [new Guid("c3030000-0000-0000-0000-000000000002")] = PermissionCode.IdentityUsersManage,
            [new Guid("c3030000-0000-0000-0000-000000000003")] = PermissionCode.IdentityOrganizationsSuspend,
            [new Guid("c3030000-0000-0000-0000-000000000004")] = PermissionCode.IdentityRolesRead,
            [new Guid("c3030000-0000-0000-0000-000000000005")] = PermissionCode.IdentityRolesAssignOrganization,
            [new Guid("c3030000-0000-0000-0000-000000000006")] = PermissionCode.IdentityRolesRevokeOrganization,
            [new Guid("c3030000-0000-0000-0000-000000000007")] = PermissionCode.IdentityServicePrincipalsRead,
            [new Guid("c3030000-0000-0000-0000-000000000008")] = PermissionCode.IdentityServicePrincipalsManage,
            [new Guid("c3030000-0000-0000-0000-000000000009")] = PermissionCode.IdentityServicePrincipalsRotateSecret,
            [new Guid("c3030000-0000-0000-0000-000000000010")] = PermissionCode.IdentityServicePrincipalsManageScopes,
            [new Guid("f7200000-0000-0000-0000-000000000001")] = PermissionCode.IdentityNavigationManage
        };

        // All canonical permissions present, and zero legacy permissions
        Assert.Equal(18, permissions.Count);
        Assert.All(permissions.Values, code => Assert.StartsWith("identity.", code, StringComparison.Ordinal));
        Assert.Equal(PermissionCode.All.OrderBy(x => x), permissions.Values.OrderBy(x => x));

        // Admin: all permissions, including navigation management
        HashSet<string> adminPerms = [.. permissions.Values];
        Assert.Equal(18, adminPerms.Count);

        // Platform Admin: all except global role assign/revoke
        HashSet<string> platformAdminPerms = [.. permissions.Values];
        platformAdminPerms.Remove(PermissionCode.IdentityRolesAssignGlobal);
        platformAdminPerms.Remove(PermissionCode.IdentityRolesRevokeGlobal);
        Assert.Equal(16, platformAdminPerms.Count);
        Assert.DoesNotContain(PermissionCode.IdentityRolesAssignGlobal, platformAdminPerms);
        Assert.DoesNotContain(PermissionCode.IdentityRolesRevokeGlobal, platformAdminPerms);

        // Owner: 7 permissions
        HashSet<string> ownerPerms =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityOrganizationsUpdate,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityMembersManage,
            PermissionCode.IdentityRolesRead,
            PermissionCode.IdentityRolesAssignOrganization,
            PermissionCode.IdentityRolesRevokeOrganization
        ];
        Assert.Equal(7, ownerPerms.Count);

        // Member: 3 permissions
        HashSet<string> memberPerms =
        [
            PermissionCode.IdentityOrganizationsRead,
            PermissionCode.IdentityMembersRead,
            PermissionCode.IdentityRolesRead
        ];
        Assert.Equal(3, memberPerms.Count);
    }

    private static IEnumerable<object> EnumerateValues(Array? array)
    {
        if (array is null)
        {
            yield break;
        }

        foreach (object? val in array)
        {
            if (val is not null)
            {
                yield return val;
            }
        }
    }
}
