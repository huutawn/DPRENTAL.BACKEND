using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkageAndCorrectPaidMilestones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "rental_item_id",
                schema: "notification",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_item_id",
                schema: "notification",
                table: "aggregate_watermarks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_rental_item",
                schema: "notification",
                table: "notifications",
                column: "rental_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_watermarks_parent_item",
                schema: "notification",
                table: "aggregate_watermarks",
                column: "parent_item_id");

            // 1. Correct PAID milestone in aggregate_watermarks:
            // PAID is a payment milestone, not a terminal state.
            migrationBuilder.Sql(@"
UPDATE notification.aggregate_watermarks
SET is_paid = true, is_terminal = false, terminal_state = NULL
WHERE terminal_state = 'PAID';
");

            // 2. Backfill is_paid = true for any order with an ORDER_PAID notification:
            // Does NOT touch real terminal states like CANCELLED/COMPLETED/EXPIRED/REJECTED.
            migrationBuilder.Sql(@"
UPDATE notification.aggregate_watermarks w
SET is_paid = true
WHERE EXISTS (
    SELECT 1 FROM notification.notifications n
    WHERE (n.notification_type = 'ORDER_PAID' OR n.notification_type = 'RENTAL_ORDER_PAID')
      AND (n.order_id = w.aggregate_id OR n.resource_id = w.aggregate_id::text)
);
");

            // 3. Backfill rental_item_id on notifications from render_data_json ->> 'rentalItemId' if valid UUID:
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET rental_item_id = (render_data_json::jsonb ->> 'rentalItemId')::uuid
WHERE rental_item_id IS NULL
  AND render_data_json IS NOT NULL
  AND render_data_json LIKE '%rentalItemId%'
  AND (render_data_json::jsonb ->> 'rentalItemId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
");

            // Also backfill rental_item_id for RentalItem resource type if resource_id is a valid UUID
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET rental_item_id = resource_id::uuid
WHERE rental_item_id IS NULL
  AND resource_type = 'RentalItem'
  AND resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
");

            // 4. Backfill parent_item_id on ReturnRequest watermarks from notifications if available:
            migrationBuilder.Sql(@"
UPDATE notification.aggregate_watermarks w
SET parent_item_id = n.rental_item_id
FROM notification.notifications n
WHERE w.scope = 'ReturnRequest'
  AND w.parent_item_id IS NULL
  AND n.rental_item_id IS NOT NULL
  AND (
      (n.resource_type = 'ReturnRequest' AND n.resource_id = w.aggregate_id::text)
      OR (n.render_data_json IS NOT NULL AND n.render_data_json LIKE '%returnRequestId%' AND (n.render_data_json::jsonb ->> 'returnRequestId') = w.aggregate_id::text)
  );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_rental_item",
                schema: "notification",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_aggregate_watermarks_parent_item",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropColumn(
                name: "rental_item_id",
                schema: "notification",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "parent_item_id",
                schema: "notification",
                table: "aggregate_watermarks");
        }
    }
}
