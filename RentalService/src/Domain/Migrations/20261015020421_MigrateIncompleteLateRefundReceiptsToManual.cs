using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MigrateIncompleteLateRefundReceiptsToManual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE rental.rental_operation_receipts r
                SET "Status" = 'MANUAL_REFUND_REQUIRED'
                FROM rental.rental_orders o
                WHERE r."RentalOrderId" = o."Id"
                  AND r."Action" = 'RequestRefund'
                  AND o."LatePaymentRefundRequired" = true
                  AND r."Status" <> 'Succeeded';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    RAISE EXCEPTION 'Downgrade is blocked: late-refund receipts migrated to MANUAL_REFUND_REQUIRED cannot be reopened automatically. Use forward-fix migration.';
                END $$;
                """);
        }
    }
}
