using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861
namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveServicePrincipalsAndScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServicePrincipalScopes",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "ServicePrincipals",
                schema: "auth");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'auth' AND table_name = 'NavigationItemPermissions') THEN
                        DELETE FROM auth."NavigationItemPermissions"
                        WHERE "PermissionId" IN (
                            SELECT "Id" FROM auth."Permissions"
                            WHERE "Code" IN (
                                'identity.service-principals.read',
                                'identity.service-principals.manage',
                                'identity.service-principals.rotate-secret',
                                'identity.service-principals.manage-scopes'
                            )
                        );
                    END IF;

                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'auth' AND table_name = 'NavigationItems') THEN
                        DELETE FROM auth."NavigationItems"
                        WHERE "Code" = 'identity.service-principals';
                    END IF;

                    DELETE FROM auth."RolePermissions"
                    WHERE "PermissionId" IN (
                        SELECT "Id" FROM auth."Permissions"
                        WHERE "Code" IN (
                            'identity.service-principals.read',
                            'identity.service-principals.manage',
                            'identity.service-principals.rotate-secret',
                            'identity.service-principals.manage-scopes'
                        )
                    );

                    DELETE FROM auth."Permissions"
                    WHERE "Code" IN (
                        'identity.service-principals.read',
                        'identity.service-principals.manage',
                        'identity.service-principals.rotate-secret',
                        'identity.service-principals.manage-scopes'
                    );
                END $$;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServicePrincipals",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePrincipals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServicePrincipalScopes",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServicePrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Audience = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePrincipalScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServicePrincipalScopes_ServicePrincipals_ServicePrincipalId",
                        column: x => x.ServicePrincipalId,
                        principalSchema: "auth",
                        principalTable: "ServicePrincipals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipals_ClientId",
                schema: "auth",
                table: "ServicePrincipals",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalScopes_ServicePrincipalId_Scope_Audience",
                schema: "auth",
                table: "ServicePrincipalScopes",
                columns: new[] { "ServicePrincipalId", "Scope", "Audience" },
                unique: true);
        }
    }
}
