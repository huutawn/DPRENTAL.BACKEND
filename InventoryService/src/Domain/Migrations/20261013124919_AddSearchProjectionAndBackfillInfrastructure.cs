using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchProjectionAndBackfillInfrastructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "search_projection_revision_seq",
                schema: "inventory");

            migrationBuilder.CreateTable(
                name: "search_backfill_runs",
                schema: "inventory",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    staging_public_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    staging_owner_listings_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    staging_owner_assets_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    starting_outbox_sequence = table.Column<long>(type: "bigint", nullable: true),
                    swap_task_uid = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_backfill_runs", x => x.run_id);
                });

            migrationBuilder.CreateTable(
                name: "search_generations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    live_public_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    live_owner_listings_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    live_owner_assets_index = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_ready = table.Column<bool>(type: "boolean", nullable: false),
                    active_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_generations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_projection_jobs",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    desired_revision = table.Column<long>(type: "bigint", nullable: false),
                    processing_revision = table.Column<long>(type: "bigint", nullable: true),
                    applied_revision = table.Column<long>(type: "bigint", nullable: false),
                    needs_asset_fanout = table.Column<bool>(type: "boolean", nullable: false),
                    fanout_cursor = table.Column<Guid>(type: "uuid", nullable: true),
                    fanout_completed = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    lock_lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lock_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pending_task_uids = table.Column<int[]>(type: "integer[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_projection_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_backfill_runs_status",
                schema: "inventory",
                table: "search_backfill_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_search_projection_jobs_claim",
                schema: "inventory",
                table: "search_projection_jobs",
                columns: new[] { "status", "next_attempt_at", "lock_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_search_projection_jobs_entity",
                schema: "inventory",
                table: "search_projection_jobs",
                columns: new[] { "entity_type", "entity_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_backfill_runs",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "search_generations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "search_projection_jobs",
                schema: "inventory");

            migrationBuilder.DropSequence(
                name: "search_projection_revision_seq",
                schema: "inventory");
        }
    }
}
