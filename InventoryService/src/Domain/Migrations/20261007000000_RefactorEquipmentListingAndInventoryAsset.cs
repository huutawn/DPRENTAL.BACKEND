using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RefactorEquipmentListingAndInventoryAsset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Guard check: Chỉ chạy khi các bảng dữ liệu runtime Inventory rỗng.
            // Nếu có dữ liệu cũ thì fail ngay trước bất kỳ thao tác phá schema nào.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    v_asset_count INT;
                    v_allocation_count INT;
                    v_block_count INT;
                BEGIN
                    SELECT COUNT(*) INTO v_asset_count FROM "inventory"."assets";
                    SELECT COUNT(*) INTO v_allocation_count FROM "inventory"."asset_allocations";
                    SELECT COUNT(*) INTO v_block_count FROM "inventory"."calendar_blocks";

                    IF v_asset_count > 0 OR v_allocation_count > 0 OR v_block_count > 0 THEN
                        RAISE EXCEPTION 'RefactorEquipmentListingAndInventoryAsset requires empty inventory tables. Old data found: assets=%, allocations=%, blocks=%. Reset/provision database before cutover.',
                            v_asset_count, v_allocation_count, v_block_count;
                    END IF;
                END $$;
                """);

            // 2. Xóa bảng runtime asset_rental_rates (giá chuyển về EquipmentListing)
            migrationBuilder.DropTable(
                name: "asset_rental_rates",
                schema: "inventory");

            // 3. Tạo bảng inventory.equipment_listings
            migrationBuilder.CreateTable(
                name: "equipment_listings",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    spec_values = table.Column<string>(type: "jsonb", nullable: false),
                    thumbnail_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    daily_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    weekly_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: true),
                    monthly_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: true),
                    currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_equipment_listings", x => x.id);
                    table.CheckConstraint("ck_equipment_listings_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid AND brand_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_equipment_listings_model", "model ~ '\\S' AND length(model) <= 200");
                    table.CheckConstraint("ck_equipment_listings_thumbnail_media_id", "thumbnail_media_id IS NULL OR thumbnail_media_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_equipment_listings_schema_version", "schema_version >= 1");
                    table.CheckConstraint("ck_equipment_listings_spec_values_object", "jsonb_typeof(spec_values) = 'object'");
                    table.CheckConstraint("ck_equipment_listings_daily_rate", "daily_rate >= 1 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");
                    table.CheckConstraint("ck_equipment_listings_weekly_rate", "weekly_rate IS NULL OR (weekly_rate >= 1 AND weekly_rate <= 999999999999999999 AND trunc(weekly_rate) = weekly_rate)");
                    table.CheckConstraint("ck_equipment_listings_monthly_rate", "monthly_rate IS NULL OR (monthly_rate >= 1 AND monthly_rate <= 999999999999999999 AND trunc(monthly_rate) = monthly_rate)");
                    table.CheckConstraint("ck_equipment_listings_currency", "currency = 'VND'");
                    table.CheckConstraint("ck_equipment_listings_status", "\"status\" IN (1, 2, 3)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_equipment_listings_owner_user_id",
                schema: "inventory",
                table: "equipment_listings",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_listings_equipment_type_id",
                schema: "inventory",
                table: "equipment_listings",
                column: "equipment_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_listings_brand_id",
                schema: "inventory",
                table: "equipment_listings",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_listings_status",
                schema: "inventory",
                table: "equipment_listings",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_listings_daily_rate",
                schema: "inventory",
                table: "equipment_listings",
                column: "daily_rate");

            // 4. Cập nhật bảng inventory.assets sang physical-only
            migrationBuilder.DropIndex(
                name: "ix_assets_owner_user_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_equipment_type_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_identity",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_brand_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_model",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_thumbnail_media_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_schema_version",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_assets_spec_values_object",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "brand_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "model",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "thumbnail_media_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "equipment_type_id",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "schema_version",
                schema: "inventory",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "spec_values",
                schema: "inventory",
                table: "assets");

            migrationBuilder.AddColumn<Guid>(
                name: "equipment_listing_id",
                schema: "inventory",
                table: "assets",
                type: "uuid",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "asset_code",
                schema: "inventory",
                table: "assets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_identity",
                schema: "inventory",
                table: "assets",
                sql: "id <> '00000000-0000-0000-0000-000000000000'::uuid AND equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_assets_asset_code",
                schema: "inventory",
                table: "assets",
                sql: "asset_code ~ '^AST-[0-9a-fA-F]{32}$'");

            migrationBuilder.AddForeignKey(
                name: "fk_assets_equipment_listings_equipment_listing_id",
                schema: "inventory",
                table: "assets",
                column: "equipment_listing_id",
                principalSchema: "inventory",
                principalTable: "equipment_listings",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "ix_assets_equipment_listing_id",
                schema: "inventory",
                table: "assets",
                column: "equipment_listing_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_asset_code",
                schema: "inventory",
                table: "assets",
                column: "asset_code",
                unique: true);

            // 5. Cập nhật bảng inventory.asset_allocations bổ sung group_reference
            migrationBuilder.AddColumn<string>(
                name: "group_reference",
                schema: "inventory",
                table: "asset_allocations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_asset_allocations_group_reference",
                schema: "inventory",
                table: "asset_allocations",
                sql: "\"group_reference\" IS NULL OR (length(btrim(\"group_reference\")) > 0 AND length(\"group_reference\") <= 100)");

            migrationBuilder.CreateIndex(
                name: "ix_asset_allocations_source_system_group_reference",
                schema: "inventory",
                table: "asset_allocations",
                columns: new[] { "source_system", "group_reference" },
                filter: "\"group_reference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asset_allocations_source_system_group_asset",
                schema: "inventory",
                table: "asset_allocations",
                columns: new[] { "source_system", "group_reference", "asset_id" },
                unique: true,
                filter: "\"group_reference\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("RefactorEquipmentListingAndInventoryAsset retains equipment_listings. Down is blocked to prevent destroying equipment listings; forward migration required.");
        }
    }
}
