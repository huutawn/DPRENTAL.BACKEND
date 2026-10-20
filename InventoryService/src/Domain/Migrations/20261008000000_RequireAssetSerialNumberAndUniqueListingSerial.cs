using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RequireAssetSerialNumberAndUniqueListingSerial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Preflight check: từ chối dữ liệu thiếu serial hoặc trùng sau chuẩn hóa
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    v_null_serial INT;
                    v_dup_serial INT;
                BEGIN
                    SELECT COUNT(*) INTO v_null_serial FROM "inventory"."assets"
                    WHERE "serial_number" IS NULL OR length(btrim("serial_number")) = 0;

                    IF v_null_serial > 0 THEN
                        RAISE EXCEPTION 'RequireAssetSerialNumberAndUniqueListingSerial requires all assets to have non-empty serial_number. Found % invalid rows.', v_null_serial;
                    END IF;

                    SELECT COUNT(*) INTO v_dup_serial FROM (
                        SELECT "equipment_listing_id", lower(btrim("serial_number"))
                        FROM "inventory"."assets"
                        GROUP BY "equipment_listing_id", lower(btrim("serial_number"))
                        HAVING COUNT(*) > 1
                    ) d;

                    IF v_dup_serial > 0 THEN
                        RAISE EXCEPTION 'RequireAssetSerialNumberAndUniqueListingSerial detected duplicate serial numbers within the same equipment listing. Found % duplicate groups.', v_dup_serial;
                    END IF;
                END $$;
                """);

            // 2. Drop constraint cũ cho phép null
            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets");

            // 3. Đổi serial_number thành NOT NULL
            migrationBuilder.AlterColumn<string>(
                name: "serial_number",
                schema: "inventory",
                table: "assets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            // 4. Thêm constraint mới bắt buộc length(btrim(serial_number)) > 0 AND length <= 100
            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets",
                sql: "length(btrim(serial_number)) > 0 AND length(serial_number) <= 100");

            // 5. Thêm unique expression index trên (equipment_listing_id, lower(btrim(serial_number)))
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "ix_assets_listing_serial_unique"
                ON "inventory"."assets" ("equipment_listing_id", lower(btrim("serial_number")));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "inventory"."ix_assets_listing_serial_unique";
                """);

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
                name: "ck_assets_serial_number",
                schema: "inventory",
                table: "assets",
                sql: "serial_number IS NULL OR (serial_number ~ '\\S' AND length(serial_number) <= 100)");
        }
    }
}
