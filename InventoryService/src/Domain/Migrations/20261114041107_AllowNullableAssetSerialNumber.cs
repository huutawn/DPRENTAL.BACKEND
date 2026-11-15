using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AllowNullableAssetSerialNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_daily_rate",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets");

            migrationBuilder.AlterColumn<string>(
                name: "serial_number",
                schema: "inventory",
                table: "assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_daily_rate",
                schema: "inventory",
                table: "equipment_listings",
                sql: "daily_rate >= 0 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets",
                sql: "serial_number IS NULL OR (length(btrim(serial_number)) > 0 AND length(serial_number) <= 100)");

            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "inventory"."ix_assets_listing_serial_unique";
                CREATE UNIQUE INDEX "ix_assets_listing_serial_unique"
                ON "inventory"."assets" ("equipment_listing_id", lower(btrim("serial_number")))
                WHERE "serial_number" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "inventory"."ix_assets_listing_serial_unique";
                CREATE UNIQUE INDEX "ix_assets_listing_serial_unique"
                ON "inventory"."assets" ("equipment_listing_id", lower(btrim("serial_number")));
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_daily_rate",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets");

            migrationBuilder.AlterColumn<string>(
                name: "serial_number",
                schema: "inventory",
                table: "assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_daily_rate",
                schema: "inventory",
                table: "equipment_listings",
                sql: "daily_rate >= 1 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets",
                sql: "length(btrim(serial_number)) > 0 AND length(serial_number) <= 100");
        }
    }
}
