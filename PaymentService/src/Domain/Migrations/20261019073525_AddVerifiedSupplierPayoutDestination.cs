using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedSupplierPayoutDestination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DestinationId",
                table: "supplier_payouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DestinationId",
                table: "financial_operation_audits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousDestinationRef",
                table: "financial_operation_audits",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supplier_payout_destinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    BankCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountHolderName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AccountNumberLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    AccountFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerificationRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuditReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payout_destinations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payouts_DestinationId",
                table: "supplier_payouts",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_destinations_DestinationRef_Version",
                table: "supplier_payout_destinations",
                columns: new[] { "DestinationRef", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_destinations_OperationKey",
                table: "supplier_payout_destinations",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payout_destinations_SupplierId_AccountFingerprint",
                table: "supplier_payout_destinations",
                columns: new[] { "SupplierId", "AccountFingerprint" });

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_payouts_supplier_payout_destinations_DestinationId",
                table: "supplier_payouts",
                column: "DestinationId",
                principalTable: "supplier_payout_destinations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reject_supplier_payout_destination_changes()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Supplier payout destination versions are immutable';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER supplier_payout_destinations_immutable
                BEFORE UPDATE OR DELETE ON supplier_payout_destinations
                FOR EACH ROW EXECUTE FUNCTION reject_supplier_payout_destination_changes();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS reject_supplier_payout_destination_changes() CASCADE;");
            migrationBuilder.DropForeignKey(
                name: "FK_supplier_payouts_supplier_payout_destinations_DestinationId",
                table: "supplier_payouts");

            migrationBuilder.DropTable(
                name: "supplier_payout_destinations");

            migrationBuilder.DropIndex(
                name: "IX_supplier_payouts_DestinationId",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "DestinationId",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "DestinationId",
                table: "financial_operation_audits");

            migrationBuilder.DropColumn(
                name: "PreviousDestinationRef",
                table: "financial_operation_audits");
        }
    }
}

