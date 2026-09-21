using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class StandardizeIdentityRbac : Migration
    {
        private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
        private static readonly Guid CustomerRoleId = new("82127c20-a094-4d8b-a1c7-b94266c001d2");
        private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
        private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");
        private static readonly Guid AdminPlatformRoleId = new("a1010000-0000-0000-0000-000000000003");

        private static readonly Guid RolesAssignPermissionId = new("f6889773-6650-4ba5-964a-97f87ab921ce");
        private static readonly Guid RolesRevokePermissionId = new("e1ff306f-1f55-498b-99ea-afa87d445a42");
        private static readonly Guid OrganizationsCreatePermissionId = new("b2020000-0000-0000-0000-000000000001");
        private static readonly Guid OrganizationsReadPermissionId = new("b2020000-0000-0000-0000-000000000002");
        private static readonly Guid OrganizationsUpdatePermissionId = new("b2020000-0000-0000-0000-000000000003");
        private static readonly Guid OrganizationsMembersReadPermissionId = new("b2020000-0000-0000-0000-000000000004");
        private static readonly Guid OrganizationsMembersManagePermissionId = new("b2020000-0000-0000-0000-000000000005");
        private static readonly Guid PlatformManagePermissionId = new("b2020000-0000-0000-0000-000000000006");

        private static readonly Guid UsersReadPermissionId = new("c3030000-0000-0000-0000-000000000001");
        private static readonly Guid UsersManagePermissionId = new("c3030000-0000-0000-0000-000000000002");
        private static readonly Guid OrganizationsSuspendPermissionId = new("c3030000-0000-0000-0000-000000000003");
        private static readonly Guid RolesReadPermissionId = new("c3030000-0000-0000-0000-000000000004");
        private static readonly Guid RolesAssignOrgPermissionId = new("c3030000-0000-0000-0000-000000000005");
        private static readonly Guid RolesRevokeOrgPermissionId = new("c3030000-0000-0000-0000-000000000006");
        private static readonly Guid SpReadPermissionId = new("c3030000-0000-0000-0000-000000000007");
        private static readonly Guid SpManagePermissionId = new("c3030000-0000-0000-0000-000000000008");
        private static readonly Guid SpRotateSecretPermissionId = new("c3030000-0000-0000-0000-000000000009");
        private static readonly Guid SpManageScopesPermissionId = new("c3030000-0000-0000-0000-000000000010");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Gỡ UserRoles dùng customer role và xoá customer role
            migrationBuilder.Sql("DELETE FROM auth.\"UserRoles\" WHERE \"RoleId\" = '82127c20-a094-4d8b-a1c7-b94266c001d2';");
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: CustomerRoleId);

            // 2. Xoá platform.manage và liên kết role-permission của nó
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [AdminRoleId, PlatformManagePermissionId]);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: PlatformManagePermissionId);

            // 3. Đổi các permission cũ sang namespace identity.*
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesAssignPermissionId, column: "Code", value: "identity.roles.assign.global");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesRevokePermissionId, column: "Code", value: "identity.roles.revoke.global");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsCreatePermissionId, column: "Code", value: "identity.organizations.create");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsReadPermissionId, column: "Code", value: "identity.organizations.read");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsUpdatePermissionId, column: "Code", value: "identity.organizations.update");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersReadPermissionId, column: "Code", value: "identity.members.read");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersManagePermissionId, column: "Code", value: "identity.members.manage");

            // 4. Thêm role admin_platform
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description", "Scope"],
                values: new object[] { AdminPlatformRoleId, "admin_platform", "Quản trị viên nền tảng, có toàn bộ quyền vận hành trừ gán/thu hồi role toàn cục.", 0 });

            // 5. Thêm 10 permission mới
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[,]
                {
                    { UsersReadPermissionId, "identity.users.read", "Xem thông tin người dùng." },
                    { UsersManagePermissionId, "identity.users.manage", "Quản lý người dùng." },
                    { OrganizationsSuspendPermissionId, "identity.organizations.suspend", "Đình chỉ hoặc kích hoạt lại organization." },
                    { RolesReadPermissionId, "identity.roles.read", "Xem thông tin role." },
                    { RolesAssignOrgPermissionId, "identity.roles.assign.organization", "Gán role trong phạm vi organization." },
                    { RolesRevokeOrgPermissionId, "identity.roles.revoke.organization", "Thu hồi role trong phạm vi organization." },
                    { SpReadPermissionId, "identity.service-principals.read", "Xem thông tin service principal." },
                    { SpManagePermissionId, "identity.service-principals.manage", "Quản lý service principal." },
                    { SpRotateSecretPermissionId, "identity.service-principals.rotate-secret", "Xoay secret của service principal." },
                    { SpManageScopesPermissionId, "identity.service-principals.manage-scopes", "Quản lý scope của service principal." }
                });

            // 6. Cấp role-permissions mới cho admin, admin_platform, Owner, Member
            migrationBuilder.InsertData(
                schema: "auth",
                table: "RolePermissions",
                columns: ["RoleId", "PermissionId"],
                values: new object[,]
                {
                    // admin: 10 quyền mới (đã có 7 quyền identity.* được đổi tên)
                    { AdminRoleId, UsersReadPermissionId },
                    { AdminRoleId, UsersManagePermissionId },
                    { AdminRoleId, OrganizationsSuspendPermissionId },
                    { AdminRoleId, RolesReadPermissionId },
                    { AdminRoleId, RolesAssignOrgPermissionId },
                    { AdminRoleId, RolesRevokeOrgPermissionId },
                    { AdminRoleId, SpReadPermissionId },
                    { AdminRoleId, SpManagePermissionId },
                    { AdminRoleId, SpRotateSecretPermissionId },
                    { AdminRoleId, SpManageScopesPermissionId },

                    // admin_platform: 15 quyền (toàn bộ trừ roles.assign.global và roles.revoke.global)
                    { AdminPlatformRoleId, OrganizationsCreatePermissionId },
                    { AdminPlatformRoleId, OrganizationsReadPermissionId },
                    { AdminPlatformRoleId, OrganizationsUpdatePermissionId },
                    { AdminPlatformRoleId, OrganizationsMembersReadPermissionId },
                    { AdminPlatformRoleId, OrganizationsMembersManagePermissionId },
                    { AdminPlatformRoleId, UsersReadPermissionId },
                    { AdminPlatformRoleId, UsersManagePermissionId },
                    { AdminPlatformRoleId, OrganizationsSuspendPermissionId },
                    { AdminPlatformRoleId, RolesReadPermissionId },
                    { AdminPlatformRoleId, RolesAssignOrgPermissionId },
                    { AdminPlatformRoleId, RolesRevokeOrgPermissionId },
                    { AdminPlatformRoleId, SpReadPermissionId },
                    { AdminPlatformRoleId, SpManagePermissionId },
                    { AdminPlatformRoleId, SpRotateSecretPermissionId },
                    { AdminPlatformRoleId, SpManageScopesPermissionId },

                    // Owner: thêm roles.read, roles.assign.org, roles.revoke.org (đã có org.read, org.update, mem.read, mem.manage)
                    { OwnerRoleId, RolesReadPermissionId },
                    { OwnerRoleId, RolesAssignOrgPermissionId },
                    { OwnerRoleId, RolesRevokeOrgPermissionId },

                    // Member: thêm roles.read (đã có org.read, mem.read)
                    { MemberRoleId, RolesReadPermissionId }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert role permissions
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [MemberRoleId, RolesReadPermissionId]);
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [OwnerRoleId, RolesReadPermissionId]);
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [OwnerRoleId, RolesAssignOrgPermissionId]);
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [OwnerRoleId, RolesRevokeOrgPermissionId]);

            // Revert admin_platform role permissions
            Guid[] adminPlatformPerms = [
                OrganizationsCreatePermissionId, OrganizationsReadPermissionId, OrganizationsUpdatePermissionId,
                OrganizationsMembersReadPermissionId, OrganizationsMembersManagePermissionId,
                UsersReadPermissionId, UsersManagePermissionId, OrganizationsSuspendPermissionId,
                RolesReadPermissionId, RolesAssignOrgPermissionId, RolesRevokeOrgPermissionId,
                SpReadPermissionId, SpManagePermissionId, SpRotateSecretPermissionId, SpManageScopesPermissionId
            ];
            foreach (Guid permId in adminPlatformPerms)
            {
                migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [AdminPlatformRoleId, permId]);
            }

            // Revert admin new role permissions
            Guid[] adminNewPerms = [
                UsersReadPermissionId, UsersManagePermissionId, OrganizationsSuspendPermissionId,
                RolesReadPermissionId, RolesAssignOrgPermissionId, RolesRevokeOrgPermissionId,
                SpReadPermissionId, SpManagePermissionId, SpRotateSecretPermissionId, SpManageScopesPermissionId
            ];
            foreach (Guid permId in adminNewPerms)
            {
                migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: [AdminRoleId, permId]);
            }

            // Revert new permissions
            foreach (Guid permId in adminNewPerms)
            {
                migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: permId);
            }

            // Revert admin_platform role
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: AdminPlatformRoleId);

            // Revert permission codes
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesAssignPermissionId, column: "Code", value: "roles.assign");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesRevokePermissionId, column: "Code", value: "roles.revoke");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsCreatePermissionId, column: "Code", value: "organizations.create");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsReadPermissionId, column: "Code", value: "organizations.read");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsUpdatePermissionId, column: "Code", value: "organizations.update");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersReadPermissionId, column: "Code", value: "organizations.members.read");
            migrationBuilder.UpdateData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersManagePermissionId, column: "Code", value: "organizations.members.manage");

            // Re-insert platform.manage
            migrationBuilder.InsertData(schema: "auth", table: "Permissions", columns: ["Id", "Code", "Description"], values: new object[] { PlatformManagePermissionId, "platform.manage", "Toàn quyền quản trị nền tảng Platform." });
            migrationBuilder.InsertData(schema: "auth", table: "RolePermissions", columns: ["RoleId", "PermissionId"], values: new object[] { AdminRoleId, PlatformManagePermissionId });

            // Re-insert customer role
            migrationBuilder.InsertData(schema: "auth", table: "Roles", columns: ["Id", "Name", "Description", "Scope"], values: new object[] { CustomerRoleId, "customer", "Role mặc định của user thường, chưa mang permission nào.", 0 });
        }
    }
}
