using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseAndListingForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "inventory",
                columns: table => new
                {
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    province_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    province_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ward_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ward_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouses", x => x.owner_user_id);
                    table.CheckConstraint("ck_warehouses_address_detail", "address_detail IS NULL OR (address_detail ~ '\\S' AND length(address_detail) <= 500)");
                    table.CheckConstraint("ck_warehouses_identity", "owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_warehouses_latitude", "latitude IS NULL OR (latitude >= -90.0 AND latitude <= 90.0)");
                    table.CheckConstraint("ck_warehouses_longitude", "longitude IS NULL OR (longitude >= -180.0 AND longitude <= 180.0)");
                    table.CheckConstraint("ck_warehouses_province_code", "province_code IS NULL OR (province_code ~ '\\S' AND length(province_code) <= 20)");
                    table.CheckConstraint("ck_warehouses_province_name", "province_name IS NULL OR (province_name ~ '\\S' AND length(province_name) <= 200)");
                    table.CheckConstraint("ck_warehouses_ward_code", "ward_code IS NULL OR (ward_code ~ '\\S' AND length(ward_code) <= 20)");
                    table.CheckConstraint("ck_warehouses_ward_name", "ward_name IS NULL OR (ward_name ~ '\\S' AND length(ward_name) <= 200)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_warehouses_province_ward",
                schema: "inventory",
                table: "warehouses",
                columns: new[] { "province_code", "ward_code" });

            // Backfill bản ghi kho mặc định cho toàn bộ owner hiện có trong equipment_listings trước khi tạo FK
            migrationBuilder.Sql("""
                INSERT INTO inventory.warehouses (owner_user_id, created_at, updated_at)
                SELECT DISTINCT owner_user_id, NOW(), NOW()
                FROM inventory.equipment_listings
                ON CONFLICT (owner_user_id) DO NOTHING;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_equipment_listings_warehouses_owner_user_id",
                schema: "inventory",
                table: "equipment_listings",
                column: "owner_user_id",
                principalSchema: "inventory",
                principalTable: "warehouses",
                principalColumn: "owner_user_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM inventory.warehouses WHERE address_detail IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot rollback migration: there are warehouses with configured addresses.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_equipment_listings_warehouses_owner_user_id",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "inventory");
        }
    }
}
