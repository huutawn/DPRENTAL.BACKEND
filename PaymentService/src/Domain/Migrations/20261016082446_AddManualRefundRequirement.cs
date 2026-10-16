using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddManualRefundRequirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "rental_item_settlements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "rental_item_settlements",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "manual_refund_requirements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RenterId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExternalTransferReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_refund_requirements", x => x.Id);
                    table.CheckConstraint("ck_manual_refund_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("ck_manual_refund_status_valid", "\"Status\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_manual_refund_requirements_rental_item_settlements_Settleme~",
                        column: x => x.SettlementId,
                        principalTable: "rental_item_settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Backfill settlements written before this migration. Their refund remains
            // a real outstanding obligation until an admin confirms the bank transfer.
            migrationBuilder.Sql("""
                UPDATE rental_item_settlements
                SET "Status" = CASE WHEN "DepositRefundDue" > 0 THEN 1 ELSE 2 END,
                    "CompletedAt" = CASE WHEN "DepositRefundDue" = 0 THEN "CreatedAt" ELSE NULL END;

                INSERT INTO manual_refund_requirements
                    ("Id", "RequirementRef", "SettlementId", "InvoiceId", "RentalItemId",
                     "RenterId", "PaymentRef", "Reason", "Amount", "Status", "CreatedAt")
                SELECT gen_random_uuid(), 'MRF-' || replace(s."Id"::text, '-', ''),
                       s."Id", s."InvoiceId", s."RentalItemId", i."RenterId",
                       i."PaymentRef", 'RENTAL_DEPOSIT_REFUND', s."DepositRefundDue", 1, s."CreatedAt"
                FROM rental_item_settlements s
                JOIN invoices i ON i."Id" = s."InvoiceId"
                WHERE s."DepositRefundDue" > 0;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_settlement_status_valid",
                table: "rental_item_settlements",
                sql: "\"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_RentalItemId",
                table: "manual_refund_requirements",
                column: "RentalItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_RequirementRef",
                table: "manual_refund_requirements",
                column: "RequirementRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_SettlementId",
                table: "manual_refund_requirements",
                column: "SettlementId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_refund_requirements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_settlement_status_valid",
                table: "rental_item_settlements");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "rental_item_settlements");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "rental_item_settlements");
        }
    }
}
