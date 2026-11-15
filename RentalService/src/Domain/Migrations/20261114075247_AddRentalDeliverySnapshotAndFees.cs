using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalDeliverySnapshotAndFees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                schema: "rental",
                table: "rental_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DeliverySnapshotJson",
                schema: "rental",
                table: "rental_orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFee",
                schema: "rental",
                table: "rental_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ReturnMethod",
                schema: "rental",
                table: "rental_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Pickup");

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                schema: "rental",
                table: "rental_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFee",
                schema: "rental",
                table: "rental_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                schema: "rental",
                table: "rental_orders");

            migrationBuilder.DropColumn(
                name: "DeliverySnapshotJson",
                schema: "rental",
                table: "rental_orders");

            migrationBuilder.DropColumn(
                name: "ReturnFee",
                schema: "rental",
                table: "rental_orders");

            migrationBuilder.DropColumn(
                name: "ReturnMethod",
                schema: "rental",
                table: "rental_orders");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "ReturnFee",
                schema: "rental",
                table: "rental_items");
        }
    }
}
