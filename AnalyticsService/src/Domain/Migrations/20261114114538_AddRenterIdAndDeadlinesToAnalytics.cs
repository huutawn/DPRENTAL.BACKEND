using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated index column arrays.
#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRenterIdAndDeadlinesToAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFeeEarned",
                table: "payment_financial_events",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFeeEarned",
                table: "payment_financial_events",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalDeliveryFee",
                table: "payment_financial_events",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalReturnFee",
                table: "payment_financial_events",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RenterId",
                table: "analytics_rental_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupplierConfirmationDeadline",
                table: "analytics_rental_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "analytics_rental_items",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFee",
                table: "analytics_rental_items",
                type: "numeric(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupplierConfirmationDeadline",
                table: "analytics_rental_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_RenterId",
                table: "analytics_rental_orders",
                column: "RenterId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_RenterId_CompletedAt",
                table: "analytics_rental_orders",
                columns: new[] { "RenterId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_SupplierConfirmationDeadl~",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "SupplierConfirmationDeadline" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_analytics_rental_orders_RenterId",
                table: "analytics_rental_orders");

            migrationBuilder.DropIndex(
                name: "IX_analytics_rental_orders_RenterId_CompletedAt",
                table: "analytics_rental_orders");

            migrationBuilder.DropIndex(
                name: "IX_analytics_rental_items_SupplierId_SupplierConfirmationDeadl~",
                table: "analytics_rental_items");

            migrationBuilder.DropColumn(
                name: "DeliveryFeeEarned",
                table: "payment_financial_events");

            migrationBuilder.DropColumn(
                name: "ReturnFeeEarned",
                table: "payment_financial_events");

            migrationBuilder.DropColumn(
                name: "TotalDeliveryFee",
                table: "payment_financial_events");

            migrationBuilder.DropColumn(
                name: "TotalReturnFee",
                table: "payment_financial_events");

            migrationBuilder.DropColumn(
                name: "RenterId",
                table: "analytics_rental_orders");

            migrationBuilder.DropColumn(
                name: "SupplierConfirmationDeadline",
                table: "analytics_rental_orders");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "analytics_rental_items");

            migrationBuilder.DropColumn(
                name: "ReturnFee",
                table: "analytics_rental_items");

            migrationBuilder.DropColumn(
                name: "SupplierConfirmationDeadline",
                table: "analytics_rental_items");
        }
    }
}
