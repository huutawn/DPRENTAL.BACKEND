using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SeedInventoryPermissions : Migration
    {
        private static readonly Guid InventoryAssetManagePermissionId = new("e5050000-0000-0000-0000-000000000002");
        private static readonly Guid InventoryCalendarReadPermissionId = new("e5050000-0000-0000-0000-000000000003");
        private static readonly Guid InventoryAllocationWritePermissionId = new("e5050000-0000-0000-0000-000000000004");

        private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
        private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[,]
                {
                    { InventoryAssetManagePermissionId, "inventory.asset.manage", "Quản lý tài sản trong kho Inventory của chính mình." },
                    { InventoryCalendarReadPermissionId, "inventory.calendar.read", "Xem chi tiết lịch tài sản của chính mình hoặc sanitized calendar cho thành viên." },
                    { InventoryAllocationWritePermissionId, "inventory.allocation.write", "Quyền gọi lệnh phân bổ/hold tài sản dành riêng cho service." }
                });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "RolePermissions",
                columns: ["RoleId", "PermissionId"],
                values: new object[,]
                {
                    { OwnerRoleId, InventoryAssetManagePermissionId },
                    { OwnerRoleId, InventoryCalendarReadPermissionId },
                    { MemberRoleId, InventoryCalendarReadPermissionId }
                });

            migrationBuilder.Sql($"""
                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT r."Id", '{InventoryAssetManagePermissionId}'
                FROM auth."Roles" r
                WHERE r."Name" = 'supplier_admin'
                  AND NOT EXISTS (
                      SELECT 1 FROM auth."RolePermissions" rp
                      WHERE rp."RoleId" = r."Id" AND rp."PermissionId" = '{InventoryAssetManagePermissionId}'
                  );

                INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                SELECT r."Id", '{InventoryCalendarReadPermissionId}'
                FROM auth."Roles" r
                WHERE r."Name" IN ('supplier_admin', 'supplier_member')
                  AND NOT EXISTS (
                      SELECT 1 FROM auth."RolePermissions" rp
                      WHERE rp."RoleId" = r."Id" AND rp."PermissionId" = '{InventoryCalendarReadPermissionId}'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM auth."RolePermissions"
                WHERE "PermissionId" IN (
                    '{InventoryAssetManagePermissionId}',
                    '{InventoryCalendarReadPermissionId}',
                    '{InventoryAllocationWritePermissionId}'
                );
                """);

            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: InventoryAssetManagePermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: InventoryCalendarReadPermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: InventoryAllocationWritePermissionId);
        }
    }
}
