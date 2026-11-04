using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddTransferRecipientAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecipientAccountHolderName",
                table: "supplier_payouts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientAccountNumber",
                table: "supplier_payouts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientBankCode",
                table: "supplier_payouts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientAccountHolderName",
                table: "manual_refund_requirements",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientAccountNumber",
                table: "manual_refund_requirements",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientBankCode",
                table: "manual_refund_requirements",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecipientAccountHolderName",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "RecipientAccountNumber",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "RecipientBankCode",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "RecipientAccountHolderName",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "RecipientAccountNumber",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "RecipientBankCode",
                table: "manual_refund_requirements");
        }
    }
}
