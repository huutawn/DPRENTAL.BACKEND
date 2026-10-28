using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861
#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalRefundConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rental_refund_confirmations",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BankCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AccountNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AccountHolderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BankTransactionRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_refund_confirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rental_refund_confirmations_rental_items_RentalItemId",
                        column: x => x.RentalItemId,
                        principalSchema: "rental",
                        principalTable: "rental_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rental_refund_confirmations_rental_orders_RentalOrderId",
                        column: x => x.RentalOrderId,
                        principalSchema: "rental",
                        principalTable: "rental_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rental_refund_confirmations_BankTransactionRef",
                schema: "rental",
                table: "rental_refund_confirmations",
                column: "BankTransactionRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_refund_confirmations_RentalItemId",
                schema: "rental",
                table: "rental_refund_confirmations",
                column: "RentalItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_refund_confirmations_RentalOrderId",
                schema: "rental",
                table: "rental_refund_confirmations",
                column: "RentalOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rental_refund_confirmations",
                schema: "rental");
        }
    }
}
