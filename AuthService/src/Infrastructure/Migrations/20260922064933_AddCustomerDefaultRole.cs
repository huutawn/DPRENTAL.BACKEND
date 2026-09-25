using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerDefaultRole : Migration
    {
        // Role toàn cục gán mặc định cho mọi user đăng ký qua /register, không mang permission nào —
        // "customer" cũ đã bị xoá ở StandardizeIdentityRbac (82127c20-...), đây là bản seed lại
        // với GUID mới, độc lập, không tái dùng id đã xoá để tránh nhầm lẫn khi đọc lịch sử migration.
        private static readonly Guid CustomerRoleId = new("c6060000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description", "Scope"],
                values: new object[] { CustomerRoleId, "customer", "Role mặc định của user thường mới đăng ký, chưa mang permission nào.", 0 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""DELETE FROM auth."UserRoles" WHERE "RoleId" = '{CustomerRoleId}';""");
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: CustomerRoleId);
        }
    }
}
