using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_transaction_resolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuditReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_transaction_resolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_transaction_resolutions_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_transaction_resolutions_payment_transactions_Transa~",
                        column: x => x.TransactionId,
                        principalTable: "payment_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_transaction_resolutions_InvoiceId",
                table: "payment_transaction_resolutions",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transaction_resolutions_OperationKey",
                table: "payment_transaction_resolutions",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transaction_resolutions_TransactionId",
                table: "payment_transaction_resolutions",
                column: "TransactionId",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION reject_payment_transaction_resolution_mutation()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Payment transaction resolutions are immutable';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_payment_transaction_resolutions_immutable
                BEFORE UPDATE OR DELETE ON payment_transaction_resolutions
                FOR EACH ROW EXECUTE FUNCTION reject_payment_transaction_resolution_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_transaction_resolutions");

            migrationBuilder.Sql("DROP FUNCTION reject_payment_transaction_resolution_mutation();");
        }
    }
}
