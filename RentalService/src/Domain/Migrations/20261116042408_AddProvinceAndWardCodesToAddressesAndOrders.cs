using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddProvinceAndWardCodesToAddressesAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvinceCode",
                schema: "rental",
                table: "renter_addresses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WardCode",
                schema: "rental",
                table: "renter_addresses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvinceCode",
                schema: "rental",
                table: "rental_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WardCode",
                schema: "rental",
                table: "rental_orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvinceCode",
                schema: "rental",
                table: "renter_addresses");

            migrationBuilder.DropColumn(
                name: "WardCode",
                schema: "rental",
                table: "renter_addresses");

            migrationBuilder.DropColumn(
                name: "ProvinceCode",
                schema: "rental",
                table: "rental_orders");

            migrationBuilder.DropColumn(
                name: "WardCode",
                schema: "rental",
                table: "rental_orders");
        }
    }
}
