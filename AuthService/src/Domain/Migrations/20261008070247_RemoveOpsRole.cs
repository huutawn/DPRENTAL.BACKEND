using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOpsRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $remove_ops$
                DECLARE
                    ops_id CONSTANT uuid := 'd4040000-0000-0000-0000-000000000002';
                BEGIN
                    LOCK TABLE auth."Roles", auth."RolePermissions", auth."UserRoles",
                        auth."OrganizationMemberships", auth."Sessions" IN SHARE ROW EXCLUSIVE MODE;

                    IF EXISTS (
                        SELECT 1 FROM auth."Roles"
                        WHERE ("Id" = ops_id AND ("Name" <> 'ops' OR "Scope" <> 0))
                           OR (lower("Name") = 'ops' AND "Id" <> ops_id)
                    ) THEN
                        RAISE EXCEPTION 'Ops seed conflict: expected canonical ID, name ops and Global scope (0).';
                    END IF;

                    IF EXISTS (SELECT 1 FROM auth."OrganizationMemberships" WHERE "RoleId" = ops_id) THEN
                        RAISE EXCEPTION 'Cannot remove ops: unexpected organization memberships require explicit migration.';
                    END IF;

                    UPDATE auth."Sessions" SET "RevokedAtUtc" = CURRENT_TIMESTAMP
                    WHERE "RevokedAtUtc" IS NULL AND "UserId" IN (
                        SELECT "UserId" FROM auth."UserRoles" WHERE "RoleId" = ops_id
                    );

                    DELETE FROM auth."UserRoles" WHERE "RoleId" = ops_id;
                    DELETE FROM auth."RolePermissions" WHERE "RoleId" = ops_id;
                    DELETE FROM auth."Roles" WHERE "Id" = ops_id;
                END
                $remove_ops$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "RemoveOpsRole cannot restore deleted assignments or revoked sessions. Restore the pre-migration backup to revert data.");
        }
    }
}
