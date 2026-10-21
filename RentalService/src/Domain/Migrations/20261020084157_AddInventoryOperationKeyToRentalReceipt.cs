#pragma warning disable CA1861
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryOperationKeyToRentalReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InventoryOperationKey",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_operation_receipts_RentalOrderId_InventoryOperationK~",
                schema: "rental",
                table: "rental_operation_receipts",
                columns: new[] { "RentalOrderId", "InventoryOperationKey" });

            // Backfill only provider keys that can be proven from the persisted request or command outbox.
            migrationBuilder.Sql("""
                UPDATE rental.rental_operation_receipts
                SET "InventoryOperationKey" = (("PayloadJson"::jsonb ->> 'providerRequest')::jsonb ->> 'operationKey')
                WHERE "InventoryOperationKey" IS NULL
                  AND "Action" = 'MarkAssetsOverdue'
                  AND "PayloadJson" IS NOT NULL
                  AND "PayloadJson"::jsonb ->> 'providerRequest' IS NOT NULL;

                UPDATE rental.rental_operation_receipts AS receipt
                SET "InventoryOperationKey" = (
                    SELECT outbox."Payload"::jsonb #>> '{payload,operationKey}'
                    FROM rental.rental_outbox_messages AS outbox
                    WHERE outbox."Topic" = 'inventory.allocation.commands.v1'
                      AND outbox."MessageType" IN ('ConfirmAllocation', 'MarkAssetsHandedOver')
                      AND outbox."Payload"::jsonb #>> '{payload,rentalOrderId}' = receipt."RentalOrderId"::text
                      AND outbox."Payload"::jsonb #>> '{payload,rentalItemId}' = receipt."TargetItemId"::text
                    ORDER BY outbox."CreatedAt" DESC
                    LIMIT 1)
                WHERE receipt."InventoryOperationKey" IS NULL
                  AND receipt."Action" IN ('SupplierAccept', 'MarkAssetsHandedOver')
                  AND receipt."TargetItemId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rental_operation_receipts_RentalOrderId_InventoryOperationK~",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "InventoryOperationKey",
                schema: "rental",
                table: "rental_operation_receipts");
        }
    }
}
