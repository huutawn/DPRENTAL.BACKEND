using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration uses inline arrays for column lists.
#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PaymentFinancialEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_financial_events",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LogicalEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AggregateId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    RenterId = table.Column<Guid>(type: "uuid", nullable: true),
                    RentalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DepositAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: true),
                    SettlementRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalDeductionAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    DepositRefundDue = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PlatformFeeRate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    PlatformFeeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    SupplierPayable = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefundRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RefundKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PayoutId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayoutRef = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_financial_events", x => x.MessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_LogicalEventId",
                table: "payment_financial_events",
                column: "LogicalEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_MessageType_OccurredAt",
                table: "payment_financial_events",
                columns: new[] { "MessageType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_RentalOrderId_MessageType",
                table: "payment_financial_events",
                columns: new[] { "RentalOrderId", "MessageType" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_SupplierId_MessageType_CompletedAt",
                table: "payment_financial_events",
                columns: new[] { "SupplierId", "MessageType", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_financial_events");
        }
    }
}
#pragma warning restore CA1861
