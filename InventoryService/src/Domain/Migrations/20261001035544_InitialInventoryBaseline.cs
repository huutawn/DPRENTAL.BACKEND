using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventoryBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateSequence(
                name: "ReservationNumberSequence",
                schema: "inventory");

            migrationBuilder.CreateTable(
                name: "equipments",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_lifecycle_status = table.Column<int>(type: "integer", nullable: false),
                    accepting_bookings = table.Column<bool>(type: "boolean", nullable: false),
                    suspension_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    lock_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregate_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    lock_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saga_instances",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    saga_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_process_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saga_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_blocks",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_blocks", x => x.id);
                    table.CheckConstraint("ck_calendar_blocks_date_order", "\"start_date\" <= \"end_date\"");
                    table.ForeignKey(
                        name: "FK_calendar_blocks_equipments_equipment_id",
                        column: x => x.equipment_id,
                        principalSchema: "inventory",
                        principalTable: "equipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_number = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('\"inventory\".\"ReservationNumberSequence\"')"),
                    equipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<int>(type: "integer", nullable: false),
                    source_system = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    renter_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    manual_renter_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    manual_renter_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    delivery_condition = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    delivery_photo_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    returned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    returned_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    return_condition = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    return_photo_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => x.id);
                    table.CheckConstraint("ck_reservations_delivery_info", "\"status\" NOT IN (3, 4) OR (\"delivered_at\" IS NOT NULL AND \"delivered_by\" IS NOT NULL)");
                    table.CheckConstraint("ck_reservations_delivery_photos_count", "cardinality(\"delivery_photo_ids\") <= 5");
                    table.CheckConstraint("ck_reservations_held_hold_expires_at", "\"status\" <> 1 OR \"hold_expires_at\" IS NOT NULL");
                    table.CheckConstraint("ck_reservations_rental_period_duration", "(\"end_date\" - \"start_date\" + 1) >= 1 AND (\"end_date\" - \"start_date\" + 1) <= 30");
                    table.CheckConstraint("ck_reservations_rental_period_order", "\"start_date\" <= \"end_date\"");
                    table.CheckConstraint("ck_reservations_return_info", "\"status\" <> 4 OR (\"returned_at\" IS NOT NULL AND \"returned_by\" IS NOT NULL)");
                    table.CheckConstraint("ck_reservations_return_photos_count", "cardinality(\"return_photo_ids\") <= 5");
                    table.ForeignKey(
                        name: "FK_reservations_equipments_equipment_id",
                        column: x => x.equipment_id,
                        principalSchema: "inventory",
                        principalTable: "equipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "damage_reports",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_usable = table.Column<bool>(type: "boolean", nullable: false),
                    photo_media_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_damage_reports", x => x.id);
                    table.CheckConstraint("ck_damage_reports_photos_count", "cardinality(\"photo_media_ids\") <= 5");
                    table.ForeignKey(
                        name: "FK_damage_reports_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservation_transitions",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<int>(type: "integer", nullable: true),
                    to_status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    previous_hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    new_hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    operation_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_transitions", x => x.id);
                    table.ForeignKey(
                        name: "FK_reservation_transitions_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_type = table.Column<int>(type: "integer", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    damage_report_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deduplication_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    viewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    viewed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_notifications_damage_reports_damage_report_id",
                        column: x => x.damage_report_id,
                        principalSchema: "inventory",
                        principalTable: "damage_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notifications_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "inventory",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_blocks_equipment_dates",
                schema: "inventory",
                table: "calendar_blocks",
                columns: new[] { "equipment_id", "start_date", "end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_reporter_account_id",
                schema: "inventory",
                table: "damage_reports",
                column: "reporter_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_damage_reports_reservation_id",
                schema: "inventory",
                table: "damage_reports",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipments_organization_id",
                schema: "inventory",
                table: "equipments",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_consumer_message",
                schema: "inventory",
                table: "inbox_messages",
                columns: new[] { "consumer", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_processed_lease",
                schema: "inventory",
                table: "inbox_messages",
                columns: new[] { "processed_at", "lock_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_damage_report_id",
                schema: "inventory",
                table: "notifications",
                column: "damage_report_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_organization_dedup",
                schema: "inventory",
                table: "notifications",
                columns: new[] { "organization_id", "deduplication_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_organization_viewed_expires",
                schema: "inventory",
                table: "notifications",
                columns: new[] { "organization_id", "viewed_at", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_reservation_id",
                schema: "inventory",
                table: "notifications",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending_retry",
                schema: "inventory",
                table: "outbox_messages",
                columns: new[] { "processed_at", "retry_count", "lock_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_transitions_reservation_operation_key",
                schema: "inventory",
                table: "reservation_transitions",
                columns: new[] { "reservation_id", "operation_key" },
                unique: true,
                filter: "\"operation_key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_transitions_reservation_sequence",
                schema: "inventory",
                table: "reservation_transitions",
                columns: new[] { "reservation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_transitions_timeline",
                schema: "inventory",
                table: "reservation_transitions",
                columns: new[] { "reservation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_equipment_status",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "equipment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_hold_expiry",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "status", "hold_expires_at" },
                filter: "\"status\" = 1 AND \"hold_expires_at\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_renter_status",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "renter_account_id", "status" },
                filter: "\"renter_account_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_reservation_number",
                schema: "inventory",
                table: "reservations",
                column: "reservation_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservations_source_system_external_reference",
                schema: "inventory",
                table: "reservations",
                columns: new[] { "source_system", "external_reference" },
                unique: true,
                filter: "\"external_reference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_next_process_state",
                schema: "inventory",
                table: "saga_instances",
                columns: new[] { "next_process_at", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_type_correlation",
                schema: "inventory",
                table: "saga_instances",
                columns: new[] { "saga_type", "correlation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_blocks",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "reservation_transitions",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "saga_instances",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "damage_reports",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "equipments",
                schema: "inventory");

            migrationBuilder.DropSequence(
                name: "ReservationNumberSequence",
                schema: "inventory");
        }
    }
}
