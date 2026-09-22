using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class TrimMvpRolesToCanonicalSet : Migration
    {
        // Bộ role toàn cục/organization chuẩn hoá còn lại sau migration này:
        // admin, admin_platform, ops, customer (Global) + Owner, Member (Organization).
        // 5 role dưới đây bị xoá vì trùng lặp hoặc thừa so với bộ role chuẩn:
        // - platform_admin: trùng admin_platform (do SeedMvpRolesAndPermissions tự nhận "tương đương admin_platform").
        // - supplier_admin/renter_admin: trùng Owner (Organization dùng chung Owner/Member cho mọi loại).
        // - supplier_member/renter_member: trùng Member.
        private static readonly Guid PlatformAdminRoleId = new("d4040000-0000-0000-0000-000000000001");
        private static readonly Guid SupplierAdminRoleId = new("d4040000-0000-0000-0000-000000000003");
        private static readonly Guid SupplierMemberRoleId = new("d4040000-0000-0000-0000-000000000004");
        private static readonly Guid RenterAdminRoleId = new("d4040000-0000-0000-0000-000000000005");
        private static readonly Guid RenterMemberRoleId = new("d4040000-0000-0000-0000-000000000006");

        private static readonly Guid AdminPlatformRoleId = new("a1010000-0000-0000-0000-000000000003");
        private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
        private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

        private static readonly Guid CatalogManagePermissionId = new("e5050000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Chuyển catalog.manage từ platform_admin sang admin_platform trước khi xoá platform_admin.
            migrationBuilder.Sql($"""
                DELETE FROM auth."RolePermissions" WHERE "RoleId" = '{PlatformAdminRoleId}' AND "PermissionId" = '{CatalogManagePermissionId}';

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{AdminPlatformRoleId}', '{CatalogManagePermissionId}'
                WHERE NOT EXISTS (
                    SELECT 1 FROM auth."RolePermissions" WHERE "RoleId" = '{AdminPlatformRoleId}' AND "PermissionId" = '{CatalogManagePermissionId}'
                );
                """);

            // 2. Reassign membership đang gán supplier_*/renter_* sang Owner/Member tương đương trước khi xoá role.
            migrationBuilder.Sql($"""
                UPDATE auth."OrganizationMemberships" SET "RoleId" = '{OwnerRoleId}'
                WHERE "RoleId" IN ('{SupplierAdminRoleId}', '{RenterAdminRoleId}');

                UPDATE auth."OrganizationMemberships" SET "RoleId" = '{MemberRoleId}'
                WHERE "RoleId" IN ('{SupplierMemberRoleId}', '{RenterMemberRoleId}');
                """);

            // 3. Reassign UserRoles đang gán platform_admin sang admin_platform (bỏ bản trùng nếu user đã có sẵn).
            migrationBuilder.Sql($"""
                DELETE FROM auth."UserRoles" ur
                WHERE ur."RoleId" = '{PlatformAdminRoleId}'
                  AND EXISTS (
                      SELECT 1 FROM auth."UserRoles" existing
                      WHERE existing."UserId" = ur."UserId" AND existing."RoleId" = '{AdminPlatformRoleId}'
                  );

                UPDATE auth."UserRoles" SET "RoleId" = '{AdminPlatformRoleId}' WHERE "RoleId" = '{PlatformAdminRoleId}';
                """);

            // 4. Xoá RolePermissions còn lại của 5 role thừa, rồi xoá chính các role đó.
            migrationBuilder.Sql($"""
                DELETE FROM auth."RolePermissions" WHERE "RoleId" IN (
                    '{PlatformAdminRoleId}', '{SupplierAdminRoleId}', '{SupplierMemberRoleId}',
                    '{RenterAdminRoleId}', '{RenterMemberRoleId}'
                );
                """);

            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: PlatformAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: SupplierAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: SupplierMemberRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: RenterAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: RenterMemberRoleId);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lưu ý: UserRoles/OrganizationMemberships đã bị reassign ở Up() không thể khôi phục
            // chính xác về role cũ (không còn lưu vết bản ghi nào từng là platform_admin/supplier_*/renter_*).
            // Down() chỉ khôi phục lại các role + permission như migration SeedMvpRolesAndPermissions gốc.
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description", "Scope"],
                values: new object[,]
                {
                    { PlatformAdminRoleId, "platform_admin", "Quản trị viên nền tảng theo mô hình MVP — tương đương admin_platform.", 0 },
                    { SupplierAdminRoleId, "supplier_admin", "Quản trị viên của một Organization loại Supplier — tương đương Owner.", 1 },
                    { SupplierMemberRoleId, "supplier_member", "Thành viên thông thường của một Organization loại Supplier — tương đương Member.", 1 },
                    { RenterAdminRoleId, "renter_admin", "Quản trị viên của một Organization loại Renter — tương đương Owner.", 1 },
                    { RenterMemberRoleId, "renter_member", "Thành viên thông thường của một Organization loại Renter — tương đương Member.", 1 }
                });

            migrationBuilder.Sql($"""
                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{PlatformAdminRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.organizations.read', 'identity.organizations.create', 'identity.organizations.update',
                    'identity.organizations.suspend', 'identity.members.read', 'identity.members.manage',
                    'identity.users.read', 'identity.users.manage', 'identity.roles.read',
                    'identity.roles.assign.organization', 'identity.roles.revoke.organization',
                    'identity.service-principals.read', 'identity.service-principals.manage',
                    'identity.service-principals.rotate-secret', 'identity.service-principals.manage-scopes'
                );

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                VALUES ('{PlatformAdminRoleId}', '{CatalogManagePermissionId}');

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{SupplierAdminRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.organizations.read', 'identity.organizations.update',
                    'identity.members.read', 'identity.members.manage', 'identity.roles.read',
                    'identity.roles.assign.organization', 'identity.roles.revoke.organization'
                );

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{SupplierMemberRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.organizations.read', 'identity.members.read', 'identity.roles.read'
                );

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{RenterAdminRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.organizations.read', 'identity.organizations.update',
                    'identity.members.read', 'identity.members.manage', 'identity.roles.read',
                    'identity.roles.assign.organization', 'identity.roles.revoke.organization'
                );

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT '{RenterMemberRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.organizations.read', 'identity.members.read', 'identity.roles.read'
                );
                """);

            migrationBuilder.Sql($"""
                DELETE FROM auth."RolePermissions" WHERE "RoleId" = '{AdminPlatformRoleId}' AND "PermissionId" = '{CatalogManagePermissionId}';
                """);
        }
    }
}
