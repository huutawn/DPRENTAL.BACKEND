using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration index column array.

#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundRequestAndOrderRejectionFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RequestedAt",
                table: "payment_financial_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RejectedAt",
                table: "analytics_rental_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_MessageType_RefundRef",
                table: "payment_financial_events",
                columns: new[] { "MessageType", "RefundRef" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_RejectedAt",
                table: "analytics_rental_orders",
                column: "RejectedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_financial_events_MessageType_RefundRef",
                table: "payment_financial_events");

            migrationBuilder.DropIndex(
                name: "IX_analytics_rental_orders_RejectedAt",
                table: "analytics_rental_orders");

            migrationBuilder.DropColumn(
                name: "RequestedAt",
                table: "payment_financial_events");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "analytics_rental_orders");
        }
    }
}
#pragma warning restore CA1861
