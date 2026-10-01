using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class HardenInventoryContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migration-owned staging table: supplied from audited evidence, consumed and removed on success.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM inventory.reservations WHERE status = 4)
                     AND to_regclass('inventory.return_condition_backfill') IS NULL THEN
                    RAISE EXCEPTION 'Completed reservations require inventory.return_condition_backfill with audited conditions; notes are not a reliable condition.';
                  END IF;
                  IF EXISTS (SELECT 1 FROM inventory.reservations WHERE
                     length(notes) > 500 OR length(manual_renter_name) > 100 OR length(manual_renter_phone) > 12 OR length(delivery_condition) > 500)
                     OR EXISTS (SELECT 1 FROM inventory.damage_reports WHERE length(btrim(description)) NOT BETWEEN 10 AND 1000) THEN
                    RAISE EXCEPTION 'Legacy text violates SRS limits; review data before migrating. No values will be truncated.';
                  END IF;
                END $$;
                """);
            migrationBuilder.RenameColumn(name: "return_condition", schema: "inventory", table: "reservations", newName: "return_condition_notes");
            migrationBuilder.AddColumn<int>(name: "return_condition", schema: "inventory", table: "reservations", type: "integer", nullable: true);
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF to_regclass('inventory.return_condition_backfill') IS NOT NULL THEN
                    EXECUTE 'UPDATE inventory.reservations r SET return_condition = b.condition
                             FROM inventory.return_condition_backfill b WHERE r.id = b.reservation_id AND r.status = 4';
                  END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                schema: "inventory",
                table: "reservations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "manual_renter_phone",
                schema: "inventory",
                table: "reservations",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "manual_renter_name",
                schema: "inventory",
                table: "reservations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "delivery_condition",
                schema: "inventory",
                table: "reservations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(name: "reason", schema: "inventory", table: "reservation_transitions",
                type: "character varying(1000)", maxLength: 1000, nullable: true,
                oldClrType: typeof(string), oldType: "character varying(500)", oldMaxLength: 500, oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_fingerprint",
                schema: "inventory",
                table: "reservation_transitions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "inventory",
                table: "damage_reports",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_damaged_evidence",
                schema: "inventory",
                table: "reservations",
                sql: "\"return_condition\" IS DISTINCT FROM 2 OR (\"return_condition_notes\" IS NOT NULL AND length(btrim(\"return_condition_notes\")) > 0 AND cardinality(\"return_photo_ids\") BETWEEN 1 AND 5)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_external_metadata",
                schema: "inventory",
                table: "reservations",
                sql: "\"source_type\" <> 2 OR (\"source_system\" IS NOT NULL AND length(btrim(\"source_system\")) > 0 AND \"external_reference\" IS NOT NULL AND length(btrim(\"external_reference\")) > 0 AND \"renter_account_id\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_return_condition",
                schema: "inventory",
                table: "reservations",
                sql: "\"status\" <> 4 OR (\"return_condition\" IS NOT NULL AND \"return_condition\" IN (1, 2))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_return_time",
                schema: "inventory",
                table: "reservations",
                sql: "\"returned_at\" IS NULL OR \"returned_at\" > \"delivered_at\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_source_status",
                schema: "inventory",
                table: "reservations",
                sql: "\"source_type\" <> 1 OR \"status\" <> 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_status",
                schema: "inventory",
                table: "reservations",
                sql: "\"status\" BETWEEN 1 AND 6 AND \"source_type\" IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_damage_reports_description",
                schema: "inventory",
                table: "damage_reports",
                sql: "length(btrim(\"description\")) BETWEEN 10 AND 1000");
            migrationBuilder.Sql("DROP TABLE IF EXISTS inventory.return_condition_backfill;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_damaged_evidence",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_external_metadata",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_return_condition",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_return_time",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_source_status",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_status",
                schema: "inventory",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_damage_reports_description",
                schema: "inventory",
                table: "damage_reports");

            migrationBuilder.DropColumn(name: "return_condition", schema: "inventory", table: "reservations");
            migrationBuilder.RenameColumn(name: "return_condition_notes", schema: "inventory", table: "reservations", newName: "return_condition");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                schema: "inventory",
                table: "reservation_transitions");

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                schema: "inventory",
                table: "reservations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "manual_renter_phone",
                schema: "inventory",
                table: "reservations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "manual_renter_name",
                schema: "inventory",
                table: "reservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "delivery_condition",
                schema: "inventory",
                table: "reservations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "reason",
                schema: "inventory",
                table: "reservation_transitions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "inventory",
                table: "damage_reports",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000);
        }
    }
}
