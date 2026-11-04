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

            // 3. Backfill rental_item_id on notifications safely using CASE pg_input_is_valid on PostgreSQL 17:
            migrationBuilder.Sql(@"
WITH safe_parsed AS (
    SELECT
        id,
        CASE
            WHEN render_data_json IS NOT NULL AND pg_input_is_valid(render_data_json, 'jsonb')
            THEN render_data_json::jsonb
            ELSE NULL
        END AS parsed_json
    FROM notification.notifications
    WHERE rental_item_id IS NULL
      AND render_data_json IS NOT NULL
),
extracted_items AS (
    SELECT
        id,
        (parsed_json ->> 'rentalItemId')::uuid AS extracted_id
    FROM safe_parsed
    WHERE parsed_json IS NOT NULL
      AND (parsed_json ? 'rentalItemId')
      AND jsonb_typeof(parsed_json -> 'rentalItemId') = 'string'
      AND (parsed_json ->> 'rentalItemId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
      AND (parsed_json ->> 'rentalItemId') <> '00000000-0000-0000-0000-000000000000'
)
UPDATE notification.notifications n
SET rental_item_id = e.extracted_id
FROM extracted_items e
WHERE n.id = e.id
  AND n.rental_item_id IS NULL;
");

            // Also backfill rental_item_id for RentalItem resource type if resource_id is a valid, non-empty UUID
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET rental_item_id = resource_id::uuid
WHERE rental_item_id IS NULL
  AND resource_type = 'RentalItem'
  AND resource_id IS NOT NULL
  AND resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  AND resource_id <> '00000000-0000-0000-0000-000000000000';
");

            // 4. Backfill parent_item_id on ReturnRequest watermarks safely with conflict detection:
            // If multiple notifications for the same ReturnRequest have conflicting rental_item_id, do NOT choose randomly.
            migrationBuilder.Sql(@"
WITH safe_notifs AS (
    SELECT
        n.id,
        n.rental_item_id,
        n.resource_type,
        n.resource_id,
        CASE
            WHEN n.render_data_json IS NOT NULL AND pg_input_is_valid(n.render_data_json, 'jsonb')
            THEN n.render_data_json::jsonb
            ELSE NULL
        END AS parsed_json
    FROM notification.notifications n
    WHERE n.rental_item_id IS NOT NULL
),
linked_pairs AS (
    SELECT
        n.rental_item_id,
        n.resource_id::uuid AS return_request_id
    FROM safe_notifs n
    WHERE n.resource_type = 'ReturnRequest'
      AND n.resource_id IS NOT NULL
      AND n.resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
      AND n.resource_id <> '00000000-0000-0000-0000-000000000000'

    UNION ALL

    SELECT
        n.rental_item_id,
        (n.parsed_json ->> 'returnRequestId')::uuid AS return_request_id
    FROM safe_notifs n
    WHERE n.parsed_json IS NOT NULL
      AND (n.parsed_json ? 'returnRequestId')
      AND jsonb_typeof(n.parsed_json -> 'returnRequestId') = 'string'
      AND (n.parsed_json ->> 'returnRequestId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
      AND (n.parsed_json ->> 'returnRequestId') <> '00000000-0000-0000-0000-000000000000'
),
single_item_links AS (
    SELECT
        return_request_id,
        MIN(rental_item_id::text)::uuid AS resolved_item_id
    FROM linked_pairs
    GROUP BY return_request_id
    HAVING COUNT(DISTINCT rental_item_id) = 1
)
UPDATE notification.aggregate_watermarks w
SET parent_item_id = s.resolved_item_id
FROM single_item_links s
WHERE w.scope = 'ReturnRequest'
  AND w.parent_item_id IS NULL
  AND w.aggregate_id = s.return_request_id;
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
