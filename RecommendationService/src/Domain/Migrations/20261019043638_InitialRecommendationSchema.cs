using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecommendationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitialRecommendationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recommendation");

            migrationBuilder.CreateTable(
                name: "interaction_events",
                schema: "recommendation",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    producer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()"),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    equipment_listing_id = table.Column<Guid>(type: "uuid", nullable: true),
                    equipment_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score_weight = table.Column<short>(type: "smallint", nullable: false),
                    envelope = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_interaction_events", x => x.message_id);
                    table.CheckConstraint("chk_interaction_events_listing_id_not_nil", "equipment_listing_id IS NULL OR equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_interaction_events_message_id_not_nil", "message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_interaction_events_producer", "producer IN ('InventoryService', 'RentalService')");
                    table.CheckConstraint("chk_interaction_events_score_weight_positive", "score_weight > 0");
                    table.CheckConstraint("chk_interaction_events_semantics", "(\n                    (message_type = 'ListingViewed' AND producer = 'InventoryService' AND score_weight = 2 AND equipment_listing_id IS NOT NULL AND equipment_type_id IS NOT NULL) OR\n                    (message_type = 'SearchPerformed' AND producer = 'InventoryService' AND score_weight = 1 AND equipment_listing_id IS NULL) OR\n                    (message_type = 'CartItemAdded' AND producer = 'RentalService' AND score_weight = 5 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL) OR\n                    (message_type = 'RentalItemPaid' AND producer = 'RentalService' AND score_weight = 8 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL) OR\n                    (message_type = 'RentalItemCompleted' AND producer = 'RentalService' AND score_weight = 10 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL)\n                )");
                    table.CheckConstraint("chk_interaction_events_type_id_not_nil", "equipment_type_id IS NULL OR equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_interaction_events_user_id_not_nil", "user_id IS NULL OR user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_interaction_events_version", "version = 1");
                });

            migrationBuilder.CreateTable(
                name: "listing_popularity",
                schema: "recommendation",
                columns: table => new
                {
                    equipment_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<long>(type: "bigint", nullable: false),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listing_popularity", x => x.equipment_listing_id);
                    table.CheckConstraint("chk_listing_popularity_id_not_nil", "equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_listing_popularity_score_positive", "score > 0");
                });

            migrationBuilder.CreateTable(
                name: "user_equipment_type_interest",
                schema: "recommendation",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<long>(type: "bigint", nullable: false),
                    last_occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_equipment_type_interest", x => new { x.user_id, x.equipment_type_id });
                    table.CheckConstraint("chk_user_interest_score_positive", "score > 0");
                    table.CheckConstraint("chk_user_interest_type_id_not_nil", "equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("chk_user_interest_user_id_not_nil", "user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateIndex(
                name: "idx_interaction_events_listing_occurred",
                schema: "recommendation",
                table: "interaction_events",
                columns: new[] { "equipment_listing_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "equipment_listing_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_interaction_events_occurred_at",
                schema: "recommendation",
                table: "interaction_events",
                column: "occurred_at",
                descending: new[] { true });

            migrationBuilder.CreateIndex(
                name: "idx_interaction_events_user_occurred",
                schema: "recommendation",
                table: "interaction_events",
                columns: new[] { "user_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_listing_popularity_ranking",
                schema: "recommendation",
                table: "listing_popularity",
                columns: new[] { "score", "equipment_listing_id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "idx_listing_popularity_updated_at",
                schema: "recommendation",
                table: "listing_popularity",
                column: "updated_at",
                descending: new[] { true });

            migrationBuilder.CreateIndex(
                name: "idx_user_interest_ranking",
                schema: "recommendation",
                table: "user_equipment_type_interest",
                columns: new[] { "user_id", "score", "equipment_type_id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "idx_user_interest_updated_at",
                schema: "recommendation",
                table: "user_equipment_type_interest",
                column: "updated_at",
                descending: new[] { true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interaction_events",
                schema: "recommendation");

            migrationBuilder.DropTable(
                name: "listing_popularity",
                schema: "recommendation");

            migrationBuilder.DropTable(
                name: "user_equipment_type_interest",
                schema: "recommendation");
        }
    }
}
