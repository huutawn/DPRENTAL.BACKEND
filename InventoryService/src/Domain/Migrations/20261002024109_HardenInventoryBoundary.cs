using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class HardenInventoryBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Maintenance cutover: pre-existing free text may contain renter PII.
            // Preserve operation keys and scheduling fingerprints; converted Return hashes
            // cannot represent a Release payload and remain reserved, non-replayable keys.
            migrationBuilder.Sql("""
                UPDATE inventory.allocation_transitions
                SET request_fingerprint = NULL
                WHERE operation_type = 7 AND reason = 'Hoàn tất kỳ phân bổ';
                UPDATE inventory.asset_allocations SET notes = NULL WHERE notes IS NOT NULL;
                UPDATE inventory.allocation_transitions SET reason = NULL WHERE reason IS NOT NULL;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_identity",
                schema: "inventory",
                table: "assets",
                sql: "id <> '00000000-0000-0000-0000-000000000000'::uuid AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND organization_id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_schema_version",
                schema: "inventory",
                table: "assets",
                sql: "schema_version >= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_spec_values_object",
                schema: "inventory",
                table: "assets",
                sql: "jsonb_typeof(spec_values) = 'object'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("HardenInventoryBoundary removes pre-cutover free text. Restore a database backup to roll back this migration.");
        }
    }
}
