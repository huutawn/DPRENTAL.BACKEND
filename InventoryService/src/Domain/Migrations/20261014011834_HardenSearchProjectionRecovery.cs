using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class HardenSearchProjectionRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "fanout_applied_revision",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "fanout_requested_revision",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "fanout_revision",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "submission_complete",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "submission_revision",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "submission_started",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "submission_uncertain",
                schema: "inventory",
                table: "search_projection_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                schema: "inventory",
                table: "search_generations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "asset_cursor",
                schema: "inventory",
                table: "search_backfill_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "assets_loaded",
                schema: "inventory",
                table: "search_backfill_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "listing_cursor",
                schema: "inventory",
                table: "search_backfill_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "listings_loaded",
                schema: "inventory",
                table: "search_backfill_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "active_slot",
                schema: "inventory",
                table: "search_backfill_runs",
                type: "integer",
                nullable: false,
                computedColumnSql: "1",
                stored: true);

            migrationBuilder.CreateTable(
                name: "search_backfill_receipts",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_backfill_receipts", x => x.id);
                });

            // Previous projections cannot prove a complete snapshot. Rebuild after draining old workers.
            migrationBuilder.Sql("""
                UPDATE inventory.search_generations SET is_ready = FALSE, active_run_id = NULL, error_code = 'SEARCH_REBUILD_REQUIRED';
                UPDATE inventory.search_backfill_runs SET status = 'Failed', completed_at = now(),
                    error_message = 'Superseded by revision-bound search recovery migration'
                WHERE status IN ('Building', 'CatchingUp');
                UPDATE inventory.search_projection_jobs SET
                    submission_started = cardinality(pending_task_uids) > 0,
                    fanout_requested_revision = CASE WHEN needs_asset_fanout THEN desired_revision ELSE 0 END,
                    fanout_applied_revision = 0, fanout_cursor = NULL, fanout_completed = FALSE;
                CREATE INDEX ix_search_outbox_run ON inventory.outbox_messages
                    ((payload->'payload'->>'runId'), created_at, id)
                    WHERE topic = 'inventory.projection.events.v1';
                """);

            migrationBuilder.CreateIndex(
                name: "ux_search_backfill_active",
                schema: "inventory",
                table: "search_backfill_runs",
                column: "active_slot",
                unique: true,
                filter: "status IN ('Building', 'CatchingUp', 'Swapping')");

            migrationBuilder.CreateIndex(
                name: "IX_search_backfill_receipts_run_id_message_id",
                schema: "inventory",
                table: "search_backfill_receipts",
                columns: new[] { "run_id", "message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX inventory.ix_search_outbox_run");

            migrationBuilder.DropTable(
                name: "search_backfill_receipts",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "ux_search_backfill_active",
                schema: "inventory",
                table: "search_backfill_runs");

            migrationBuilder.DropColumn(
                name: "active_slot",
                schema: "inventory",
                table: "search_backfill_runs");

            migrationBuilder.DropColumn(
                name: "fanout_applied_revision",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "fanout_requested_revision",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "fanout_revision",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "submission_complete",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "submission_revision",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "submission_started",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "submission_uncertain",
                schema: "inventory",
                table: "search_projection_jobs");

            migrationBuilder.DropColumn(
                name: "error_code",
                schema: "inventory",
                table: "search_generations");

            migrationBuilder.DropColumn(
                name: "asset_cursor",
                schema: "inventory",
                table: "search_backfill_runs");

            migrationBuilder.DropColumn(
                name: "assets_loaded",
                schema: "inventory",
                table: "search_backfill_runs");

            migrationBuilder.DropColumn(
                name: "listing_cursor",
                schema: "inventory",
                table: "search_backfill_runs");

            migrationBuilder.DropColumn(
                name: "listings_loaded",
                schema: "inventory",
                table: "search_backfill_runs");
        }
    }
}
