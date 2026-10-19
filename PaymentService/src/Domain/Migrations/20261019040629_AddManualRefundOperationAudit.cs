using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddManualRefundOperationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "manual_refund_operation_audits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RefundRequirementId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    ExternalTransferReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_refund_operation_audits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_manual_refund_operation_audits_manual_refund_requirements_R~",
                        column: x => x.RefundRequirementId,
                        principalTable: "manual_refund_requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM manual_refund_requirements
                        WHERE "ExternalTransferReference" IS NOT NULL
                        GROUP BY "ExternalTransferReference"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Duplicate manual refund transfer references must be reconciled before migration';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_manual_refund_requirements_ExternalTransferReference",
                table: "manual_refund_requirements",
                column: "ExternalTransferReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_operation_audits_OperationKey",
                table: "manual_refund_operation_audits",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_operation_audits_RefundRequirementId_Occurred~",
                table: "manual_refund_operation_audits",
                columns: new[] { "RefundRequirementId", "OccurredAt" });

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_manual_refund_operation_audit_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Manual refund operation audits are immutable';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_manual_refund_operation_audits_immutable
                BEFORE UPDATE OR DELETE ON manual_refund_operation_audits
                FOR EACH ROW EXECUTE FUNCTION reject_manual_refund_operation_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_refund_operation_audits");

            migrationBuilder.Sql("DROP FUNCTION reject_manual_refund_operation_audit_mutation();");

            migrationBuilder.DropIndex(
                name: "UX_manual_refund_requirements_ExternalTransferReference",
                table: "manual_refund_requirements");
        }
    }
}
