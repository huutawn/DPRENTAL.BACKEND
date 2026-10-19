using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierPayoutCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompletedBy",
                table: "supplier_payouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTransferReference",
                table: "supplier_payouts",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_operation_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DestinationRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExternalTransferReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_financial_operation_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_financial_operation_audits_supplier_payouts_PayoutId",
                        column: x => x.PayoutId,
                        principalTable: "supplier_payouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_supplier_payouts_ExternalTransferReference",
                table: "supplier_payouts",
                column: "ExternalTransferReference",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_payouts_completion_consistent",
                table: "supplier_payouts",
                sql: "(\"Status\" = 'PENDING_PAYOUT' AND \"CompletedAt\" IS NULL AND \"CompletedBy\" IS NULL AND \"ExternalTransferReference\" IS NULL) OR (\"Status\" = 'COMPLETED' AND \"CompletedAt\" IS NOT NULL AND \"CompletedBy\" IS NOT NULL AND \"ExternalTransferReference\" IS NOT NULL AND \"DestinationRef\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_payouts_status_valid",
                table: "supplier_payouts",
                sql: "\"Status\" IN ('PENDING_PAYOUT', 'COMPLETED')");

            migrationBuilder.CreateIndex(
                name: "IX_financial_operation_audits_OperationKey",
                table: "financial_operation_audits",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_financial_operation_audits_PayoutId_OccurredAt",
                table: "financial_operation_audits",
                columns: new[] { "PayoutId", "OccurredAt" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_financial_operation_audit_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Financial operation audits are immutable';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_financial_operation_audits_immutable
                BEFORE UPDATE OR DELETE ON financial_operation_audits
                FOR EACH ROW EXECUTE FUNCTION reject_financial_operation_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_operation_audits");

            migrationBuilder.Sql("DROP FUNCTION reject_financial_operation_audit_mutation();");

            migrationBuilder.DropIndex(
                name: "UX_supplier_payouts_ExternalTransferReference",
                table: "supplier_payouts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_payouts_completion_consistent",
                table: "supplier_payouts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_payouts_status_valid",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "ExternalTransferReference",
                table: "supplier_payouts");
        }
    }
}
