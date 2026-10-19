using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialReadIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_supplier_payouts_SupplierId_CreatedAt",
                table: "supplier_payouts",
                columns: new[] { "SupplierId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_item_settlements_Status_CreatedAt",
                table: "rental_item_settlements",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_Status_CreatedAt",
                table: "manual_refund_requirements",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_supplier_payouts_SupplierId_CreatedAt",
                table: "supplier_payouts");

            migrationBuilder.DropIndex(
                name: "IX_rental_item_settlements_Status_CreatedAt",
                table: "rental_item_settlements");

            migrationBuilder.DropIndex(
                name: "IX_manual_refund_requirements_Status_CreatedAt",
                table: "manual_refund_requirements");
        }
    }
}
