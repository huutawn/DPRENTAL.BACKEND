using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CorrectNotificationLinkageBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Safe data correction backfill for rental_item_id on notifications (WHERE rental_item_id IS NULL)
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

            // 2. Also backfill rental_item_id for RentalItem resource type if resource_id is a valid, non-empty UUID and rental_item_id is still null
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET rental_item_id = resource_id::uuid
WHERE rental_item_id IS NULL
  AND resource_type = 'RentalItem'
  AND resource_id IS NOT NULL
  AND resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  AND resource_id <> '00000000-0000-0000-0000-000000000000';
");

            // 3. Safe data correction backfill for parent_item_id on ReturnRequest watermarks (WHERE parent_item_id IS NULL)
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
            // Data-only correction migration: Down() does not delete backfilled linkage fields
            // because database cannot distinguish backfilled data from application data created after migration.
        }
    }
}
