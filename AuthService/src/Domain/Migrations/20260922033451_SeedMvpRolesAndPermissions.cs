using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SeedMvpRolesAndPermissions : Migration
    {
        // Role hiện có (admin, admin_platform, Owner, Member) được GIỮ NGUYÊN — 6 role MVP dưới đây
        // là bổ sung song song theo đúng mô hình nghiệp vụ (platform_admin/ops toàn cục,
        // supplier_*/renter_* theo Organization), không thay thế gì cả.
        private static readonly Guid PlatformAdminRoleId = new("d4040000-0000-0000-0000-000000000001");
        private static readonly Guid OpsRoleId = new("d4040000-0000-0000-0000-000000000002");
        private static readonly Guid SupplierAdminRoleId = new("d4040000-0000-0000-0000-000000000003");
        private static readonly Guid SupplierMemberRoleId = new("d4040000-0000-0000-0000-000000000004");
        private static readonly Guid RenterAdminRoleId = new("d4040000-0000-0000-0000-000000000005");
        private static readonly Guid RenterMemberRoleId = new("d4040000-0000-0000-0000-000000000006");

        // catalog.manage: permission nền cho một service khác (Catalog) chưa tồn tại — cố ý KHÔNG
        // mang namespace "identity.*" (PermissionCode.cs chỉ chứa permission của chính AuthService),
        // nên không có hằng số C# tương ứng, chỉ tồn tại dưới dạng dữ liệu trong bảng Permissions.
        private static readonly Guid CatalogManagePermissionId = new("e5050000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description", "Scope"],
                values: new object[,]
                {
                    { PlatformAdminRoleId, "platform_admin", "Quản trị viên nền tảng theo mô hình MVP — tương đương admin_platform.", 0 },
                    { OpsRoleId, "ops", "Nhân viên vận hành nền tảng — chỉ có quyền xem, hỗ trợ người dùng.", 0 },
                    { SupplierAdminRoleId, "supplier_admin", "Quản trị viên của một Organization loại Supplier — tương đương Owner.", 1 },
                    { SupplierMemberRoleId, "supplier_member", "Thành viên thông thường của một Organization loại Supplier — tương đương Member.", 1 },
                    { RenterAdminRoleId, "renter_admin", "Quản trị viên của một Organization loại Renter — tương đương Owner.", 1 },
                    { RenterMemberRoleId, "renter_member", "Thành viên thông thường của một Organization loại Renter — tương đương Member.", 1 }
                }); 

            //hi
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[,]
                {
                    { CatalogManagePermissionId, "catalog.manage", "Quản lý catalog — quyền nền chuẩn bị cho service Catalog trong tương lai, không thuộc AuthService." }
                });

            // Gán permission cho role mới bằng cách tra theo Code (không hardcode GUID của permission
            // đã có sẵn từ trước, tránh sai sót) — permission.identity.* nào role đã có thì gán y hệt.
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
                SELECT '{OpsRoleId}', "Id" FROM auth."Permissions" WHERE "Code" IN (
                    'identity.users.read', 'identity.organizations.read', 'identity.members.read',
                    'identity.roles.read', 'identity.service-principals.read'
                );

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM auth."RolePermissions" WHERE "RoleId" IN (
                    '{PlatformAdminRoleId}', '{OpsRoleId}', '{SupplierAdminRoleId}',
                    '{SupplierMemberRoleId}', '{RenterAdminRoleId}', '{RenterMemberRoleId}'
                );
                """);

            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: CatalogManagePermissionId);

            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: PlatformAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: OpsRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: SupplierAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: SupplierMemberRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: RenterAdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: RenterMemberRoleId);
        }
    }
}
