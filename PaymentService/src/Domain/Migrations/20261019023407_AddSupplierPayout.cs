using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierPayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_payouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PayoutRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DestinationRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payouts", x => x.Id);
                    table.CheckConstraint("CK_supplier_payouts_amount_positive", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_supplier_payouts_rental_item_settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "rental_item_settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Older settlements were recorded before payout tracking existed.
            // Carry their payable amount forward as a real pending obligation.
            migrationBuilder.Sql("""
                INSERT INTO supplier_payouts
                    ("Id", "PayoutRef", "SettlementId", "RentalItemId", "SupplierId",
                     "Amount", "Currency", "DestinationRef", "Status", "CreatedAt", "CompletedAt")
                SELECT gen_random_uuid(), 'PO-' || replace(s."Id"::text, '-', ''),
                       s."Id", s."RentalItemId", s."SupplierId", s."SupplierPayable",
                       i."Currency", NULL, 'PENDING_PAYOUT', s."CreatedAt", NULL
                FROM rental_item_settlements s
                JOIN invoices i ON i."Id" = s."InvoiceId"
                WHERE s."SupplierPayable" > 0
                  AND NOT EXISTS (
                      SELECT 1 FROM supplier_payouts p WHERE p."SettlementId" = s."Id"
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payouts_Status_CreatedAt",
                table: "supplier_payouts",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_supplier_payouts_PayoutRef",
                table: "supplier_payouts",
                column: "PayoutRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_supplier_payouts_RentalItemId_SupplierId",
                table: "supplier_payouts",
                columns: new[] { "RentalItemId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_supplier_payouts_SettlementId",
                table: "supplier_payouts",
                column: "SettlementId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_payouts");
        }
    }
}
