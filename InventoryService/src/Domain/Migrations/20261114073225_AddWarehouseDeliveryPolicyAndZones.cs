using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseDeliveryPolicyAndZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "close_time",
                schema: "inventory",
                table: "warehouses",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                defaultValue: "18:00");

            migrationBuilder.AddColumn<int[]>(
                name: "closed_days",
                schema: "inventory",
                table: "warehouses",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'::integer[]");

            migrationBuilder.AddColumn<bool>(
                name: "is_pickup_enabled",
                schema: "inventory",
                table: "warehouses",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "open_time",
                schema: "inventory",
                table: "warehouses",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                defaultValue: "08:00");

            migrationBuilder.CreateTable(
                name: "warehouse_delivery_zones",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    province_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ward_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    delivery_fee_vnd = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    return_fee_vnd = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_delivery_zones", x => x.id);
                    table.CheckConstraint("ck_warehouse_delivery_zones_delivery_fee", "delivery_fee_vnd >= 0 AND delivery_fee_vnd <= 999999999999999999 AND trunc(delivery_fee_vnd) = delivery_fee_vnd");
                    table.CheckConstraint("ck_warehouse_delivery_zones_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_warehouse_delivery_zones_province_code", "province_code ~ '\\S' AND length(province_code) <= 20");
                    table.CheckConstraint("ck_warehouse_delivery_zones_return_fee", "return_fee_vnd >= 0 AND return_fee_vnd <= 999999999999999999 AND trunc(return_fee_vnd) = return_fee_vnd");
                    table.CheckConstraint("ck_warehouse_delivery_zones_ward_code", "ward_code IS NULL OR (ward_code ~ '\\S' AND length(ward_code) <= 20)");
                    table.ForeignKey(
                        name: "FK_warehouse_delivery_zones_warehouses_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "inventory",
                        principalTable: "warehouses",
                        principalColumn: "owner_user_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_warehouses_close_time",
                schema: "inventory",
                table: "warehouses",
                sql: "close_time IS NULL OR close_time ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_warehouses_open_time",
                schema: "inventory",
                table: "warehouses",
                sql: "open_time IS NULL OR open_time ~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'");

            migrationBuilder.CreateIndex(
                name: "ix_warehouse_delivery_zones_owner_province_ward",
                schema: "inventory",
                table: "warehouse_delivery_zones",
                columns: new[] { "owner_user_id", "province_code", "ward_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouse_delivery_zones",
                schema: "inventory");

            migrationBuilder.DropCheckConstraint(
                name: "ck_warehouses_close_time",
                schema: "inventory",
                table: "warehouses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_warehouses_open_time",
                schema: "inventory",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "close_time",
                schema: "inventory",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "closed_days",
                schema: "inventory",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "is_pickup_enabled",
                schema: "inventory",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "open_time",
                schema: "inventory",
                table: "warehouses");
        }
    }
}
