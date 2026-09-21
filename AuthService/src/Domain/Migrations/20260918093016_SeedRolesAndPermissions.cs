using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SeedRolesAndPermissions : Migration
    {
        private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
        private static readonly Guid CustomerRoleId = new("82127c20-a094-4d8b-a1c7-b94266c001d2");
        private static readonly Guid RolesAssignPermissionId = new("f6889773-6650-4ba5-964a-97f87ab921ce");
        private static readonly Guid RolesRevokePermissionId = new("e1ff306f-1f55-498b-99ea-afa87d445a42");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description"],
                values: new object[,]
                {
                    { AdminRoleId, "admin", "Toàn quyền quản trị AuthService — bao gồm gán/thu hồi role." },
                    { CustomerRoleId, "customer", "Role mặc định của user thường, chưa mang permission nào." }
                });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[,]
                {
                    { RolesAssignPermissionId, "roles.assign", "Gán role cho một user khác." },
                    { RolesRevokePermissionId, "roles.revoke", "Thu hồi role khỏi một user khác." }
                });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "RolePermissions",
                columns: ["RoleId", "PermissionId"],
                values: new object[,]
                {
                    { AdminRoleId, RolesAssignPermissionId },
                    { AdminRoleId, RolesRevokePermissionId }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, RolesAssignPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, RolesRevokePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesAssignPermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: RolesRevokePermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: AdminRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: CustomerRoleId);
        }
    }
}
