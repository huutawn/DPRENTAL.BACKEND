using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated index column arrays.
#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardRentalFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_processed_rental_events",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_processed_rental_events", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "analytics_rental_items",
                columns: table => new
                {
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    EquipmentTypeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    BrandName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    RentalAmount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    DepositAmount = table.Column<decimal>(type: "numeric(18,0)", precision: 18, scale: 0, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiryReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HandoverAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReturnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_rental_items", x => x.RentalItemId);
                });

            migrationBuilder.CreateTable(
                name: "analytics_rental_orders",
                columns: table => new
                {
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    StatusVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_rental_orders", x => x.RentalOrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_BrandId",
                table: "analytics_rental_items",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_EquipmentListingId",
                table: "analytics_rental_items",
                column: "EquipmentListingId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_EquipmentTypeId",
                table: "analytics_rental_items",
                column: "EquipmentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_RentalOrderId",
                table: "analytics_rental_items",
                column: "RentalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_CompletedAt",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_ConfirmedAt",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "ConfirmedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_ExpiredAt",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "ExpiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_RejectedAt",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "RejectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_CancelledAt",
                table: "analytics_rental_orders",
                column: "CancelledAt");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_CompletedAt",
                table: "analytics_rental_orders",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_CreatedAt",
                table: "analytics_rental_orders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_ExpiredAt",
                table: "analytics_rental_orders",
                column: "ExpiredAt");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_PaidAt",
                table: "analytics_rental_orders",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_orders_Status_PaidAt",
                table: "analytics_rental_orders",
                columns: new[] { "Status", "PaidAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_processed_rental_events");

            migrationBuilder.DropTable(
                name: "analytics_rental_items");

            migrationBuilder.DropTable(
                name: "analytics_rental_orders");
        }
    }
}
#pragma warning restore CA1861
