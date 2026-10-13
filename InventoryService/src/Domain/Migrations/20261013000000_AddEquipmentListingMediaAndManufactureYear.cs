using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentListingMediaAndManufactureYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Thêm cột media_ids mặc định danh sách rỗng và manufacture_year nullable
            migrationBuilder.AddColumn<Guid[]>(
                name: "media_ids",
                schema: "inventory",
                table: "equipment_listings",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<int>(
                name: "manufacture_year",
                schema: "inventory",
                table: "equipment_listings",
                type: "integer",
                nullable: true);

            // 2. Backfill media_ids từ thumbnail_media_id hiện có (nếu khác null)
            migrationBuilder.Sql("""
                UPDATE "inventory"."equipment_listings"
                SET "media_ids" = ARRAY["thumbnail_media_id"]
                WHERE "thumbnail_media_id" IS NOT NULL
                  AND cardinality("media_ids") = 0;
                """);

            // 3. Backfill manufacture_year từ asset tạo sớm nhất (tie-break bằng id ASC);
            // giữ nguyên năm của asset, nếu asset sớm nhất chưa có năm hoặc listing không có asset thì để null
            migrationBuilder.Sql("""
                UPDATE "inventory"."equipment_listings" l
                SET "manufacture_year" = earliest."manufacture_year"
                FROM (
                    SELECT DISTINCT ON ("equipment_listing_id")
                        "equipment_listing_id",
                        "manufacture_year"
                    FROM "inventory"."assets"
                    ORDER BY "equipment_listing_id", "created_at" ASC, "id" ASC
                ) AS earliest
                WHERE l."id" = earliest."equipment_listing_id"
                  AND l."manufacture_year" IS NULL;
                """);

            // 4. Thêm check constraints cho manufacture_year và media_ids
            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_manufacture_year",
                schema: "inventory",
                table: "equipment_listings",
                sql: "manufacture_year IS NULL OR manufacture_year >= 1900");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_media_ids",
                schema: "inventory",
                table: "equipment_listings",
                sql: "cardinality(media_ids) <= 20 AND array_position(media_ids, NULL) IS NULL AND NOT ('00000000-0000-0000-0000-000000000000'::uuid = ANY(media_ids)) AND (thumbnail_media_id IS NULL OR thumbnail_media_id = ANY(media_ids))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_media_ids",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_manufacture_year",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropColumn(
                name: "media_ids",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropColumn(
                name: "manufacture_year",
                schema: "inventory",
                table: "equipment_listings");
        }
    }
}
