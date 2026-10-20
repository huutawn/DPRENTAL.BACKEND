using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentListingNameAndDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Thêm cột name (tạm thời nullable để backfill) và description (nullable)
            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "inventory",
                table: "equipment_listings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "inventory",
                table: "equipment_listings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // 2. Backfill name bằng model cho dữ liệu cũ, để description = null
            migrationBuilder.Sql("""
                UPDATE "inventory"."equipment_listings"
                SET "name" = "model"
                WHERE "name" IS NULL;
                """);

            // 3. Đặt name thành NOT NULL
            migrationBuilder.AlterColumn<string>(
                name: "name",
                schema: "inventory",
                table: "equipment_listings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            // 4. Thêm check constraints cho name và description
            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_name",
                schema: "inventory",
                table: "equipment_listings",
                sql: "name ~ '\\S' AND length(name) <= 200");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_description",
                schema: "inventory",
                table: "equipment_listings",
                sql: "description IS NULL OR (description ~ '\\S' AND length(description) <= 2000)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_name",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_description",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "inventory",
                table: "equipment_listings");
        }
    }
}
