using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsReadModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_dead_letters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Partition = table.Column<int>(type: "integer", nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_dead_letters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "analytics_inbox_messages",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Topic = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Partition = table.Column<int>(type: "integer", nullable: false),
                    Offset = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_inbox_messages", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "financial_settlement_facts",
                columns: table => new
                {
                    SettlementRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    RentalAmount = table.Column<long>(type: "bigint", nullable: true),
                    DepositAmount = table.Column<long>(type: "bigint", nullable: true),
                    PlatformFeeAmount = table.Column<long>(type: "bigint", nullable: true),
                    SupplierPayable = table.Column<long>(type: "bigint", nullable: true),
                    FinalDeductionAmount = table.Column<long>(type: "bigint", nullable: false),
                    DepositRefundDue = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_settlement_facts", x => x.SettlementRef);
                });

            migrationBuilder.CreateTable(
                name: "refund_facts",
                columns: table => new
                {
                    RefundRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundedRentalAmount = table.Column<long>(type: "bigint", nullable: false),
                    RefundedDepositAmount = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refund_facts", x => new { x.RefundRef, x.RentalItemId });
                });

            migrationBuilder.CreateTable(
                name: "rental_item_facts",
                columns: table => new
                {
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    EquipmentListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    EquipmentTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    EquipmentTypeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    BrandName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    RentalAmount = table.Column<long>(type: "bigint", nullable: true),
                    DepositAmount = table.Column<long>(type: "bigint", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiryReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HandoverAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReturnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastOrderVersion = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_item_facts", x => x.RentalItemId);
                });

            migrationBuilder.CreateTable(
                name: "rental_order_facts",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RenterId = table.Column<Guid>(type: "uuid", nullable: true),
                    RentalAmount = table.Column<long>(type: "bigint", nullable: true),
                    DepositAmount = table.Column<long>(type: "bigint", nullable: true),
                    ItemCount = table.Column<int>(type: "integer", nullable: true),
                    SupplierCount = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastVersion = table.Column<long>(type: "bigint", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_order_facts", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "supplier_payout_facts",
                columns: table => new
                {
                    PayoutRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payout_facts", x => x.PayoutRef);
                });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_dead_letters_Topic_Partition_Offset",
                table: "analytics_dead_letters",
                columns: new[] { "Topic", "Partition", "Offset" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_financial_settlement_facts_CompletedAt",
                table: "financial_settlement_facts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_financial_settlement_facts_RentalItemId",
                table: "financial_settlement_facts",
                column: "RentalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_financial_settlement_facts_SupplierId_CompletedAt",
                table: "financial_settlement_facts",
                columns: new[] { "SupplierId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_refund_facts_CompletedAt",
                table: "refund_facts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_refund_facts_OrderId",
                table: "refund_facts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_refund_facts_RentalItemId",
                table: "refund_facts",
                column: "RentalItemId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_BrandId_PaidAt",
                table: "rental_item_facts",
                columns: new[] { "BrandId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_CompletedAt",
                table: "rental_item_facts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_EquipmentListingId_PaidAt",
                table: "rental_item_facts",
                columns: new[] { "EquipmentListingId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_EquipmentTypeId_PaidAt",
                table: "rental_item_facts",
                columns: new[] { "EquipmentTypeId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_HandoverAt",
                table: "rental_item_facts",
                column: "HandoverAt");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_OrderId",
                table: "rental_item_facts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_Status",
                table: "rental_item_facts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_facts_SupplierId_PaidAt",
                table: "rental_item_facts",
                columns: new[] { "SupplierId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_order_facts_CompletedAt",
                table: "rental_order_facts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_rental_order_facts_CreatedAt",
                table: "rental_order_facts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_rental_order_facts_PaidAt",
                table: "rental_order_facts",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_rental_order_facts_Status",
                table: "rental_order_facts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_facts_CompletedAt",
                table: "supplier_payout_facts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_facts_Status",
                table: "supplier_payout_facts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_facts_SupplierId_CompletedAt",
                table: "supplier_payout_facts",
                columns: new[] { "SupplierId", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_dead_letters");

            migrationBuilder.DropTable(
                name: "analytics_inbox_messages");

            migrationBuilder.DropTable(
                name: "financial_settlement_facts");

            migrationBuilder.DropTable(
                name: "refund_facts");

            migrationBuilder.DropTable(
                name: "rental_item_facts");

            migrationBuilder.DropTable(
                name: "rental_order_facts");

            migrationBuilder.DropTable(
                name: "supplier_payout_facts");
        }
    }
}
