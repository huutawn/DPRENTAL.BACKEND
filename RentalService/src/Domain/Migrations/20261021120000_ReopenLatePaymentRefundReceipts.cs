using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations;

[DbContext(typeof(RentalServiceDbContext))]
[Migration("20261021120000_ReopenLatePaymentRefundReceipts")]
public sealed class ReopenLatePaymentRefundReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE rental.rental_operation_receipts AS r
            SET "Status" = 'Pending',
                "IsSuccess" = FALSE,
                "CompletedAt" = NULL,
                "NextRetryAt" = NOW(),
                "ErrorCode" = NULL,
                "ErrorMessage" = NULL
            WHERE r."Action" = 'RequestRefund'
              AND r."Status" = 'MANUAL_REFUND_REQUIRED'
              AND EXISTS (
                  SELECT 1
                  FROM rental.rental_orders AS o
                  WHERE o."Id" = r."RentalOrderId"
                    AND o."LatePaymentRefundRequired" = TRUE
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM rental.rental_operation_receipts AS r
                    JOIN rental.rental_orders AS o ON o."Id" = r."RentalOrderId"
                    WHERE r."Action" = 'RequestRefund'
                      AND r."Status" = 'Pending'
                      AND o."LatePaymentRefundRequired" = TRUE
                ) THEN
                    RAISE EXCEPTION 'Downgrade is blocked while late-payment refund receipts are retryable; reconcile them before rolling back.';
                END IF;
            END $$;
            """);
    }
}
