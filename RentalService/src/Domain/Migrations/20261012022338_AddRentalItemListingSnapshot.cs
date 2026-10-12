using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalItemListingSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BrandId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "BrandNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "EquipmentTypeId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "EquipmentTypeNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SpecSchemaVersionSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SpecValuesSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<Guid>(
                name: "ThumbnailMediaIdSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrandId",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "BrandNameSnapshot",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "EquipmentTypeId",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "EquipmentTypeNameSnapshot",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "ModelSnapshot",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "SpecSchemaVersionSnapshot",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "SpecValuesSnapshot",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "ThumbnailMediaIdSnapshot",
                schema: "rental",
                table: "rental_items");
        }
    }
}
