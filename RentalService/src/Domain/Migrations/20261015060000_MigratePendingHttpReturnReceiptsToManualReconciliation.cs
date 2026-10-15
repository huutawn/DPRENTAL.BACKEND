#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace Dprental.RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MigratePendingHttpReturnReceiptsToManualReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE rental.rental_operation_receipts
                SET "Status" = 'MANUAL_RECONCILIATION_REQUIRED'
                WHERE "Action" IN ('MarkAssetsReturned', 'BlockDamagedAssets')
                  AND "Status" <> 'Succeeded';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    RAISE EXCEPTION 'Downgrade is blocked: return receipts migrated to MANUAL_RECONCILIATION_REQUIRED cannot be reopened automatically. Use forward-fix migration.';
                END $$;
                """);
        }
    }
}
