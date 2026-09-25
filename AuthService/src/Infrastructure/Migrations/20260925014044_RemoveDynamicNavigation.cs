using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861
namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDynamicNavigation : Migration
    {
        private static readonly Guid NavigationManagePermissionId = new("f7200000-0000-0000-0000-000000000001");
        private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
        private static readonly Guid AdminPlatformRoleId = new("a1010000-0000-0000-0000-000000000003");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "auth",
                table: "RolePermissions",
                keyColumns: ["RoleId", "PermissionId"],
                keyValues: new object[] { AdminRoleId, NavigationManagePermissionId });

            migrationBuilder.DeleteData(
                schema: "auth",
                table: "RolePermissions",
                keyColumns: ["RoleId", "PermissionId"],
                keyValues: new object[] { AdminPlatformRoleId, NavigationManagePermissionId });

            migrationBuilder.DropTable(
                name: "NavigationItemPermissions",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "NavigationItems",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "NavigationCatalogs",
                schema: "auth");

            migrationBuilder.DeleteData(
                schema: "auth",
                table: "Permissions",
                keyColumn: "Id",
                keyValue: NavigationManagePermissionId);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NavigationCatalogs",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DashboardType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavigationCatalogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavigationCatalogs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "auth",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NavigationItems",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Href = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Icon = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    SourceTemplateItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavigationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NavigationItems_NavigationCatalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalSchema: "auth",
                        principalTable: "NavigationCatalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NavigationItems_NavigationItems_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "auth",
                        principalTable: "NavigationItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NavigationItemPermissions",
                schema: "auth",
                columns: table => new
                {
                    NavigationItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NavigationItemPermissions", x => new { x.NavigationItemId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_NavigationItemPermissions_NavigationItems_NavigationItemId",
                        column: x => x.NavigationItemId,
                        principalSchema: "auth",
                        principalTable: "NavigationItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NavigationItemPermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "auth",
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NavigationCatalogs_DashboardType",
                schema: "auth",
                table: "NavigationCatalogs",
                column: "DashboardType",
                unique: true,
                filter: "\"OrganizationId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NavigationCatalogs_OrganizationId",
                schema: "auth",
                table: "NavigationCatalogs",
                column: "OrganizationId",
                unique: true,
                filter: "\"OrganizationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_NavigationItemPermissions_PermissionId",
                schema: "auth",
                table: "NavigationItemPermissions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_NavigationItems_CatalogId_Key",
                schema: "auth",
                table: "NavigationItems",
                columns: new[] { "CatalogId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NavigationItems_ParentId",
                schema: "auth",
                table: "NavigationItems",
                column: "ParentId");

            migrationBuilder.InsertData(
                schema: "auth",
                table: "Permissions",
                columns: ["Id", "Code", "Description"],
                values: new object[] { NavigationManagePermissionId, "identity.navigation.manage", "Quản lý template và catalog navigation dashboard." });

            migrationBuilder.InsertData(
                schema: "auth",
                table: "RolePermissions",
                columns: ["RoleId", "PermissionId"],
                values: new object[,]
                {
                    { AdminRoleId, NavigationManagePermissionId },
                    { AdminPlatformRoleId, NavigationManagePermissionId }
                });
        }
    }
}
