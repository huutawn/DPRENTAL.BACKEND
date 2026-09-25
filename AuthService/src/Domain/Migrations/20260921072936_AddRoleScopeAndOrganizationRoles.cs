using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleScopeAndOrganizationRoles : Migration
    {
        private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
        private static readonly Guid OwnerRoleId = new("a1010000-0000-0000-0000-000000000001");
        private static readonly Guid MemberRoleId = new("a1010000-0000-0000-0000-000000000002");

        private static readonly Guid OrganizationsCreatePermissionId = new("b2020000-0000-0000-0000-000000000001");
        private static readonly Guid OrganizationsReadPermissionId = new("b2020000-0000-0000-0000-000000000002");
        private static readonly Guid OrganizationsUpdatePermissionId = new("b2020000-0000-0000-0000-000000000003");
        private static readonly Guid OrganizationsMembersReadPermissionId = new("b2020000-0000-0000-0000-000000000004");
        private static readonly Guid OrganizationsMembersManagePermissionId = new("b2020000-0000-0000-0000-000000000005");
        private static readonly Guid PlatformManagePermissionId = new("b2020000-0000-0000-0000-000000000006");
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Scope",
                schema: "auth",
                table: "Roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.InsertData(
                schema: "auth",
                table: "Roles",
                columns: ["Id", "Name", "Description", "Scope"],
                values: new object[,]
                {
                    { OwnerRoleId, "Owner", "Toàn quyền quản trị trong phạm vi Organization.", 1 },
                    { MemberRoleId, "Member", "Thành viên thông thường trong phạm vi Organization với quyền xem.", 1 }
                });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[,]
                {
                    { OrganizationsCreatePermissionId, "organizations.create", "Tạo organization mới." },
                    { OrganizationsReadPermissionId, "organizations.read", "Xem thông tin organization." },
                    { OrganizationsUpdatePermissionId, "organizations.update", "Cập nhật thông tin organization." },
                    { OrganizationsMembersReadPermissionId, "organizations.members.read", "Xem danh sách thành viên trong organization." },
                    { OrganizationsMembersManagePermissionId, "organizations.members.manage", "Quản lý thành viên trong organization." },
                    { PlatformManagePermissionId, "platform.manage", "Toàn quyền quản trị nền tảng Platform." }
                });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "RolePermissions",
                columns: ["RoleId", "PermissionId"],
                values: new object[,]
                {
                    { AdminRoleId, OrganizationsCreatePermissionId },
                    { AdminRoleId, OrganizationsReadPermissionId },
                    { AdminRoleId, OrganizationsUpdatePermissionId },
                    { AdminRoleId, OrganizationsMembersReadPermissionId },
                    { AdminRoleId, OrganizationsMembersManagePermissionId },
                    { AdminRoleId, PlatformManagePermissionId },
                    { OwnerRoleId, OrganizationsReadPermissionId },
                    { OwnerRoleId, OrganizationsUpdatePermissionId },
                    { OwnerRoleId, OrganizationsMembersReadPermissionId },
                    { OwnerRoleId, OrganizationsMembersManagePermissionId },
                    { MemberRoleId, OrganizationsReadPermissionId },
                    { MemberRoleId, OrganizationsMembersReadPermissionId }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, OrganizationsCreatePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, OrganizationsReadPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, OrganizationsUpdatePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, OrganizationsMembersReadPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, OrganizationsMembersManagePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, PlatformManagePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { OwnerRoleId, OrganizationsReadPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { OwnerRoleId, OrganizationsUpdatePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { OwnerRoleId, OrganizationsMembersReadPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { OwnerRoleId, OrganizationsMembersManagePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { MemberRoleId, OrganizationsReadPermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { MemberRoleId, OrganizationsMembersReadPermissionId });

            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsCreatePermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsReadPermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsUpdatePermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersReadPermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: OrganizationsMembersManagePermissionId);
            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: PlatformManagePermissionId);

            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: OwnerRoleId);
            migrationBuilder.DeleteData(schema: "auth", table: "Roles", keyColumn: "Id", keyValue: MemberRoleId);

            migrationBuilder.DropColumn(
                name: "Scope",
                schema: "auth",
                table: "Roles");
        }
    }
}
