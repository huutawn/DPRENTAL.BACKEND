using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AlignRentalModelAfterDevelopMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "rental",
                table: "rental_cart_items",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "rental",
                table: "rental_cart_items",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.Sql("ALTER TABLE rental.rental_cart_items ALTER COLUMN \"CreatedAt\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE rental.rental_cart_items ALTER COLUMN \"UpdatedAt\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "rental",
                table: "rental_cart_items");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "rental",
                table: "rental_cart_items");
        }
    }
}
