using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddNavigationCatalogCrud : Migration
    {
        private static readonly Guid NavigationManagePermissionId = new("f7200000-0000-0000-0000-000000000001");
        private static readonly Guid AdminRoleId = new("fba7cef9-3ad7-49f5-8c2b-f9364945fcc4");
        private static readonly Guid AdminPlatformRoleId = new("a1010000-0000-0000-0000-000000000003");
        private static readonly Guid PlatformTemplateId = new("f7100000-0000-0000-0000-000000000001");
        private static readonly Guid SupplierTemplateId = new("f7100000-0000-0000-0000-000000000002");
        private static readonly Guid RenterTemplateId = new("f7100000-0000-0000-0000-000000000003");
        private static readonly Guid IdentityItemId = new("f7110000-0000-0000-0000-000000000001");
        private static readonly Guid UsersItemId = new("f7110000-0000-0000-0000-000000000002");
        private static readonly Guid OrganizationsItemId = new("f7110000-0000-0000-0000-000000000003");
        private static readonly Guid MembersItemId = new("f7110000-0000-0000-0000-000000000004");
        private static readonly Guid RolesItemId = new("f7110000-0000-0000-0000-000000000005");
        private static readonly Guid ServicePrincipalsItemId = new("f7110000-0000-0000-0000-000000000006");
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NavigationCatalogs",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DashboardType = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
                    SourceTemplateItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Href = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Icon = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
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

            DateTime seededAt = new(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                schema: "auth",
                table: "NavigationCatalogs",
                columns: ["Id", "OrganizationId", "DashboardType", "CreatedAtUtc"],
                values: new object[,]
                {
                    { PlatformTemplateId, null, 0, seededAt },
                    { SupplierTemplateId, null, 1, seededAt },
                    { RenterTemplateId, null, 2, seededAt }
                });
            migrationBuilder.InsertData(
                schema: "auth",
                table: "NavigationItems",
                columns: ["Id", "CatalogId", "ParentId", "SourceTemplateItemId", "Key", "Label", "Href", "Icon", "Order", "IsActive"],
                values: new object[,]
                {
                    { IdentityItemId, PlatformTemplateId, null, null, "identity", "Identity", null, "shield", 10, true },
                    { UsersItemId, PlatformTemplateId, IdentityItemId, null, "identity.users", "Users", "/users", "users", 10, true },
                    { OrganizationsItemId, PlatformTemplateId, IdentityItemId, null, "identity.organizations", "Organizations", "/organizations", "building-2", 20, true },
                    { MembersItemId, PlatformTemplateId, IdentityItemId, null, "identity.members", "Members", "/organizations/{organizationId}/members", "users-round", 30, true },
                    { RolesItemId, PlatformTemplateId, IdentityItemId, null, "identity.roles", "Roles", "/roles", "shield-check", 40, true },
                    { ServicePrincipalsItemId, PlatformTemplateId, IdentityItemId, null, "identity.service-principals", "Service Principals", "/service-principals", "bot", 50, true }
                });
            migrationBuilder.Sql($"""
                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT '{UsersItemId}', "Id" FROM auth."Permissions" WHERE "Code" IN ('identity.users.read', 'identity.users.manage');
                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT '{OrganizationsItemId}', "Id" FROM auth."Permissions" WHERE "Code" IN ('identity.organizations.read', 'identity.organizations.create', 'identity.organizations.update', 'identity.organizations.suspend');
                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT '{MembersItemId}', "Id" FROM auth."Permissions" WHERE "Code" IN ('identity.members.read', 'identity.members.manage');
                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT '{RolesItemId}', "Id" FROM auth."Permissions" WHERE "Code" IN ('identity.roles.read', 'identity.roles.assign.global', 'identity.roles.revoke.global', 'identity.roles.assign.organization', 'identity.roles.revoke.organization');
                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT '{ServicePrincipalsItemId}', "Id" FROM auth."Permissions" WHERE "Code" IN ('identity.service-principals.read', 'identity.service-principals.manage', 'identity.service-principals.rotate-secret', 'identity.service-principals.manage-scopes');

                INSERT INTO auth."NavigationCatalogs" ("Id", "OrganizationId", "DashboardType", "CreatedAtUtc")
                SELECT md5('navigation-catalog:' || "Id"::text)::uuid, "Id", "Type", NOW()
                FROM auth."Organizations";

                INSERT INTO auth."NavigationItems" ("Id", "CatalogId", "ParentId", "SourceTemplateItemId", "Key", "Label", "Href", "Icon", "Order", "IsActive")
                SELECT md5('navigation-item:' || catalog."Id"::text || ':' || template."Id"::text)::uuid, catalog."Id", NULL, template."Id", template."Key", template."Label", template."Href", template."Icon", template."Order", template."IsActive"
                FROM auth."NavigationCatalogs" catalog
                JOIN auth."Organizations" organization ON organization."Id" = catalog."OrganizationId"
                JOIN auth."NavigationItems" template ON template."CatalogId" = '{PlatformTemplateId}'
                WHERE organization."Type" = 0;

                UPDATE auth."NavigationItems" child
                SET "ParentId" = parent_copy."Id"
                FROM auth."NavigationItems" template, auth."NavigationItems" parent_copy
                WHERE child."SourceTemplateItemId" = template."Id"
                  AND template."ParentId" IS NOT NULL
                  AND parent_copy."CatalogId" = child."CatalogId"
                  AND parent_copy."SourceTemplateItemId" = template."ParentId";

                INSERT INTO auth."NavigationItemPermissions" ("NavigationItemId", "PermissionId")
                SELECT copy."Id", binding."PermissionId"
                FROM auth."NavigationItems" copy
                JOIN auth."NavigationItemPermissions" binding ON binding."NavigationItemId" = copy."SourceTemplateItemId"
                WHERE copy."SourceTemplateItemId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminRoleId, NavigationManagePermissionId });
            migrationBuilder.DeleteData(schema: "auth", table: "RolePermissions", keyColumns: ["RoleId", "PermissionId"], keyValues: new object[] { AdminPlatformRoleId, NavigationManagePermissionId });
            migrationBuilder.DropTable(
                name: "NavigationItemPermissions",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "NavigationItems",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "NavigationCatalogs",
                schema: "auth");

            migrationBuilder.DeleteData(schema: "auth", table: "Permissions", keyColumn: "Id", keyValue: NavigationManagePermissionId);
        }
    }
}
#pragma warning restore CA1861
