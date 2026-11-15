using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddShippingFeesAndSettlementBreakdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_settlement_amounts_non_negative",
                table: "rental_item_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_settlement_money_balance",
                table: "rental_item_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_item_amounts",
                table: "manual_refund_items");

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFeeEarned",
                table: "rental_item_settlements",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFeeEarned",
                table: "rental_item_settlements",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "manual_refund_items",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFee",
                table: "manual_refund_items",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalDeliveryFee",
                table: "invoices",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalReturnFee",
                table: "invoices",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "invoice_items",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnFee",
                table: "invoice_items",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_settlement_amounts_non_negative",
                table: "rental_item_settlements",
                sql: "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"FinalDeductionAmount\" >= 0 AND \"DepositRefundDue\" >= 0 AND \"PlatformFeeAmount\" >= 0 AND \"SupplierPayable\" >= 0 AND \"DeliveryFeeEarned\" >= 0 AND \"ReturnFeeEarned\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_settlement_money_balance",
                table: "rental_item_settlements",
                sql: "\"SupplierPayable\" + \"PlatformFeeAmount\" + \"DepositRefundDue\" = \"RentalAmount\" + \"DepositAmount\" + \"DeliveryFeeEarned\" + \"ReturnFeeEarned\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_item_amounts",
                table: "manual_refund_items",
                sql: "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"DeliveryFee\" >= 0 AND \"ReturnFee\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_settlement_amounts_non_negative",
                table: "rental_item_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_settlement_money_balance",
                table: "rental_item_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_refund_item_amounts",
                table: "manual_refund_items");

            migrationBuilder.DropColumn(
                name: "DeliveryFeeEarned",
                table: "rental_item_settlements");

            migrationBuilder.DropColumn(
                name: "ReturnFeeEarned",
                table: "rental_item_settlements");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "manual_refund_items");

            migrationBuilder.DropColumn(
                name: "ReturnFee",
                table: "manual_refund_items");

            migrationBuilder.DropColumn(
                name: "TotalDeliveryFee",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "TotalReturnFee",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "invoice_items");

            migrationBuilder.DropColumn(
                name: "ReturnFee",
                table: "invoice_items");

            migrationBuilder.AddCheckConstraint(
                name: "ck_settlement_amounts_non_negative",
                table: "rental_item_settlements",
                sql: "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0 AND \"FinalDeductionAmount\" >= 0 AND \"DepositRefundDue\" >= 0 AND \"PlatformFeeAmount\" >= 0 AND \"SupplierPayable\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_settlement_money_balance",
                table: "rental_item_settlements",
                sql: "\"SupplierPayable\" + \"PlatformFeeAmount\" + \"DepositRefundDue\" = \"RentalAmount\" + \"DepositAmount\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_refund_item_amounts",
                table: "manual_refund_items",
                sql: "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0");
        }
    }
}
