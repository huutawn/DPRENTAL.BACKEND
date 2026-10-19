using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalItemSettlementLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rental_item_settlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RentalAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    DepositAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    FinalDeductionAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    DepositRefundDue = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    PlatformFeeRate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    PlatformFeeAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    SupplierPayable = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_item_settlements", x => x.Id);
                    table.CheckConstraint("ck_settlement_amounts_non_negative", "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"FinalDeductionAmount\" >= 0 AND \"DepositRefundDue\" >= 0 AND \"PlatformFeeAmount\" >= 0 AND \"SupplierPayable\" >= 0");
                    table.CheckConstraint("ck_settlement_deduction_le_deposit", "\"FinalDeductionAmount\" <= \"DepositAmount\"");
                    table.CheckConstraint("ck_settlement_fee_rate_range", "\"PlatformFeeRate\" >= 0 AND \"PlatformFeeRate\" <= 1");
                    table.CheckConstraint("ck_settlement_money_balance", "\"SupplierPayable\" + \"PlatformFeeAmount\" + \"DepositRefundDue\" = \"RentalAmount\" + \"DepositAmount\"");
                    table.ForeignKey(
                        name: "FK_rental_item_settlements_invoice_items_InvoiceItemId",
                        column: x => x.InvoiceItemId,
                        principalTable: "invoice_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "settlement_ledger_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settlement_ledger_entries", x => x.Id);
                    table.CheckConstraint("ck_settlement_entry_amount_non_negative", "\"Amount\" >= 0");
                    table.ForeignKey(
                        name: "FK_settlement_ledger_entries_rental_item_settlements_Settlemen~",
                        column: x => x.SettlementId,
                        principalTable: "rental_item_settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_settlements_InvoiceItemId",
                table: "rental_item_settlements",
                column: "InvoiceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_settlements_OperationKey",
                table: "rental_item_settlements",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_settlements_RentalItemId",
                table: "rental_item_settlements",
                column: "RentalItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_settlements_SettlementRef",
                table: "rental_item_settlements",
                column: "SettlementRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_settlement_ledger_entries_SettlementId_EntryType",
                table: "settlement_ledger_entries",
                columns: new[] { "SettlementId", "EntryType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "settlement_ledger_entries");

            migrationBuilder.DropTable(
                name: "rental_item_settlements");
        }
    }
}
