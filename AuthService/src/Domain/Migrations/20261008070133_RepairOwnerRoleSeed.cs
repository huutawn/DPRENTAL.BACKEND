using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RepairOwnerRoleSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair seed drift without replacing existing roles, grants or account relationships.
            migrationBuilder.Sql(
                """
                DO $repair_owner$
                DECLARE
                    owner_id CONSTANT uuid := 'a1010000-0000-0000-0000-000000000001';
                    permission_seed record;
                BEGIN
                    LOCK TABLE auth."Roles", auth."Permissions", auth."RolePermissions"
                        IN SHARE ROW EXCLUSIVE MODE;

                    IF EXISTS (
                        SELECT 1 FROM auth."Roles"
                        WHERE ("Id" = owner_id AND ("Name" <> 'Owner' OR "Scope" <> 1))
                           OR (lower("Name") = 'owner' AND "Id" <> owner_id)
                    ) THEN
                        RAISE EXCEPTION 'Owner seed conflict: expected canonical ID, name Owner and Organization scope (1).';
                    END IF;

                    INSERT INTO auth."Roles" ("Id", "Name", "Description", "Scope")
                    VALUES (owner_id, 'Owner', 'Chủ tổ chức; quyền áp dụng trong phạm vi tổ chức.', 1)
                    ON CONFLICT ("Id") DO NOTHING;

                    FOR permission_seed IN
                        SELECT * FROM (VALUES
                            ('b2020000-0000-0000-0000-000000000002', 'identity.organizations.read', 'Xem thông tin organization.'),
                            ('b2020000-0000-0000-0000-000000000003', 'identity.organizations.update', 'Cập nhật thông tin organization.'),
                            ('b2020000-0000-0000-0000-000000000004', 'identity.members.read', 'Xem danh sách thành viên trong organization.'),
                            ('b2020000-0000-0000-0000-000000000005', 'identity.members.manage', 'Quản lý thành viên trong organization.'),
                            ('c3030000-0000-0000-0000-000000000004', 'identity.roles.read', 'Xem thông tin role.'),
                            ('c3030000-0000-0000-0000-000000000005', 'identity.roles.assign.organization', 'Gán role trong phạm vi organization.'),
                            ('c3030000-0000-0000-0000-000000000006', 'identity.roles.revoke.organization', 'Thu hồi role trong phạm vi organization.'),
                            ('e5050000-0000-0000-0000-000000000002', 'inventory.asset.manage', 'Quản lý tài sản trong kho Inventory của chính mình.'),
                            ('e5050000-0000-0000-0000-000000000003', 'inventory.calendar.read', 'Xem lịch tài sản trong phạm vi quyền được cấp.')
                        ) AS required_permissions(id, code, description)
                    LOOP
                        IF EXISTS (
                            SELECT 1 FROM auth."Permissions"
                            WHERE "Id" = permission_seed.id::uuid AND "Code" <> permission_seed.code
                        ) THEN
                            RAISE EXCEPTION 'Owner permission seed conflict for code %.', permission_seed.code;
                        END IF;

                        INSERT INTO auth."Permissions" ("Id", "Code", "Description")
                        VALUES (permission_seed.id::uuid, permission_seed.code, permission_seed.description)
                        ON CONFLICT ("Code") DO NOTHING;

                        INSERT INTO auth."RolePermissions" ("RoleId", "PermissionId")
                        SELECT owner_id, "Id" FROM auth."Permissions" WHERE "Code" = permission_seed.code
                        ON CONFLICT ("RoleId", "PermissionId") DO NOTHING;
                    END LOOP;
                END
                $repair_owner$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Up cannot distinguish pre-existing seed from repaired seed after it commits.
            throw new NotSupportedException(
                "RepairOwnerRoleSeed cannot be downgraded safely. Restore the pre-migration backup to revert seed data.");
        }
    }
}
