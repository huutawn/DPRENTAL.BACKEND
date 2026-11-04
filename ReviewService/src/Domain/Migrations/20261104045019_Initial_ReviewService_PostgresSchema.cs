using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReviewService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class Initial_ReviewService_PostgresSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    http_method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_path = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    response_status_code = table.Column<int>(type: "integer", nullable: false),
                    response_headers = table.Column<string>(type: "text", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_group = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.message_id, x.consumer_group });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    partition_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_checkpoints",
                columns: table => new
                {
                    sweep_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_completed = table.Column<bool>(type: "boolean", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_checkpoints", x => x.sweep_id);
                });

            migrationBuilder.CreateTable(
                name: "review_projections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    renter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_eligible = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_projections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    author_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                    moderation_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Visible"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    admin_hidden_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    admin_reason = table.Column<string>(type: "text", nullable: true),
                    can_edit_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.id);
                    table.CheckConstraint("chk_reviews_author_state", "author_state IN ('Active', 'Withdrawn')");
                    table.CheckConstraint("chk_reviews_direction", "direction IN ('renter_to_supplier_listing', 'supplier_to_renter')");
                    table.CheckConstraint("chk_reviews_moderation_state", "moderation_state IN ('Visible', 'Hidden')");
                    table.CheckConstraint("chk_reviews_rating", "rating >= 1 AND rating <= 5");
                });

            migrationBuilder.CreateTable(
                name: "review_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    old_values = table.Column<string>(type: "jsonb", nullable: true),
                    new_values = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_audits", x => x.id);
                    table.CheckConstraint("chk_audits_action", "action IN ('Created', 'Updated', 'Withdrawn', 'Hidden', 'Restored', 'Reported', 'AdminHidden', 'AdminRestored')");
                    table.ForeignKey(
                        name: "fk_audits_review",
                        column: x => x.review_id,
                        principalTable: "reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_media", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_media_review",
                        column: x => x.review_id,
                        principalTable: "reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_reports", x => x.id);
                    table.CheckConstraint("chk_reports_status", "status IN ('PendingReview', 'ReviewedDismissed', 'ReviewedHidden')");
                    table.ForeignKey(
                        name: "fk_reports_review",
                        column: x => x.review_id,
                        principalTable: "reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_idempotency_expires",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "uq_idempotency_actor_key",
                table: "idempotency_records",
                columns: new[] { "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outbox_processed",
                table: "outbox_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "idx_audits_actor_id",
                table: "review_audits",
                columns: new[] { "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_audits_review_id",
                table: "review_audits",
                columns: new[] { "review_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "uq_review_media_pair",
                table: "review_media",
                columns: new[] { "review_id", "media_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_projections_renter_eligible",
                table: "review_projections",
                columns: new[] { "renter_id", "is_eligible" });

            migrationBuilder.CreateIndex(
                name: "idx_projections_supplier_eligible",
                table: "review_projections",
                columns: new[] { "supplier_id", "is_eligible" });

            migrationBuilder.CreateIndex(
                name: "uq_projections_item_id",
                table: "review_projections",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_reports_status",
                table: "review_reports",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_reports_review_reporter",
                table: "review_reports",
                columns: new[] { "review_id", "reporter_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_reviews_author",
                table: "reviews",
                columns: new[] { "author_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_reviews_listing_active_visible",
                table: "reviews",
                columns: new[] { "equipment_listing_id", "created_at" },
                filter: "direction = 'renter_to_supplier_listing' AND author_state = 'Active' AND moderation_state = 'Visible'");

            migrationBuilder.CreateIndex(
                name: "idx_reviews_subject_private",
                table: "reviews",
                columns: new[] { "subject_id", "created_at" },
                filter: "direction = 'supplier_to_renter'");

            migrationBuilder.CreateIndex(
                name: "uq_reviews_item_direction",
                table: "reviews",
                columns: new[] { "item_id", "direction" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "reconciliation_checkpoints");

            migrationBuilder.DropTable(
                name: "review_audits");

            migrationBuilder.DropTable(
                name: "review_media");

            migrationBuilder.DropTable(
                name: "review_projections");

            migrationBuilder.DropTable(
                name: "review_reports");

            migrationBuilder.DropTable(
                name: "reviews");
        }
    }
}
