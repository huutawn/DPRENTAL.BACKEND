using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddKafkaFoundationAndSagaRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_saga_instances_next_process_state",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropIndex(
                name: "ix_saga_instances_type_correlation",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending_retry",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "aggregate_id",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "aggregate_type",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "event_type",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "event_version",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.RenameColumn(
                name: "next_process_at",
                schema: "inventory",
                table: "saga_instances",
                newName: "timeout_lease_expires_at");

            migrationBuilder.RenameColumn(
                name: "processed_at",
                schema: "inventory",
                table: "outbox_messages",
                newName: "published_at");

            migrationBuilder.RenameColumn(
                name: "consumer",
                schema: "inventory",
                table: "inbox_messages",
                newName: "handler");

            migrationBuilder.RenameIndex(
                name: "ix_inbox_messages_consumer_message",
                schema: "inventory",
                table: "inbox_messages",
                newName: "ix_inbox_messages_handler_message");

            migrationBuilder.Sql("""
                ALTER TABLE inventory.saga_instances
                ALTER COLUMN state TYPE integer
                USING CASE state
                    WHEN 'Started' THEN 1
                    WHEN 'Executing' THEN 2
                    WHEN 'Completed' THEN 3
                    WHEN 'Compensating' THEN 4
                    WHEN 'Failed' THEN 5
                    ELSE 1
                END;
                """);

            migrationBuilder.AddColumn<string>(
                name: "current_step",
                schema: "inventory",
                table: "saga_instances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                schema: "inventory",
                table: "saga_instances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "timeout_deadline",
                schema: "inventory",
                table: "saga_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "timeout_id",
                schema: "inventory",
                table: "saga_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "timeout_last_error",
                schema: "inventory",
                table: "saga_instances",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "timeout_lease_token",
                schema: "inventory",
                table: "saga_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "timeout_retry_count",
                schema: "inventory",
                table: "saga_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "headers",
                schema: "inventory",
                table: "outbox_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kafka_key",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "lock_lease_token",
                schema: "inventory",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "inventory",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "topic",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(250)",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "inventory",
                table: "inbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_timeout_state",
                schema: "inventory",
                table: "saga_instances",
                columns: new[] { "timeout_deadline", "state", "timeout_lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_saga_instances_type_org_correlation",
                schema: "inventory",
                table: "saga_instances",
                columns: new[] { "saga_type", "organization_id", "correlation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_key_ordering",
                schema: "inventory",
                table: "outbox_messages",
                columns: new[] { "kafka_key", "published_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending_claim",
                schema: "inventory",
                table: "outbox_messages",
                columns: new[] { "published_at", "next_attempt_at", "lock_lease_expires_at", "retry_count", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_status_next_attempt",
                schema: "inventory",
                table: "inbox_messages",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_saga_instances_timeout_state",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropIndex(
                name: "ix_saga_instances_type_org_correlation",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_key_ordering",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_pending_claim",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_inbox_messages_status_next_attempt",
                schema: "inventory",
                table: "inbox_messages");

            migrationBuilder.DropColumn(
                name: "current_step",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "organization_id",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "timeout_deadline",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "timeout_id",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "timeout_last_error",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "timeout_lease_token",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "timeout_retry_count",
                schema: "inventory",
                table: "saga_instances");

            migrationBuilder.DropColumn(
                name: "headers",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "kafka_key",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "lock_lease_token",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "topic",
                schema: "inventory",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "inventory",
                table: "inbox_messages");

            migrationBuilder.RenameColumn(
                name: "timeout_lease_expires_at",
                schema: "inventory",
                table: "saga_instances",
                newName: "next_process_at");

            migrationBuilder.RenameColumn(
                name: "published_at",
                schema: "inventory",
                table: "outbox_messages",
                newName: "processed_at");

            migrationBuilder.RenameColumn(
                name: "handler",
                schema: "inventory",
                table: "inbox_messages",
                newName: "consumer");

            migrationBuilder.RenameIndex(
                name: "ix_inbox_messages_handler_message",
                schema: "inventory",
                table: "inbox_messages",
                newName: "ix_inbox_messages_consumer_message");

            migrationBuilder.Sql("""
                ALTER TABLE inventory.saga_instances
                ALTER COLUMN state TYPE character varying(50)
                USING CASE state
                    WHEN 1 THEN 'Started'
                    WHEN 2 THEN 'Executing'
                    WHEN 3 THEN 'Completed'
                    WHEN 4 THEN 'Compensating'
                    WHEN 5 THEN 'Failed'
                    ELSE 'Started'
                END;
                """);

            migrationBuilder.AddColumn<string>(
                name: "aggregate_id",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "aggregate_type",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_type",
                schema: "inventory",
                table: "outbox_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "event_version",
                schema: "inventory",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

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

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending_retry",
                schema: "inventory",
                table: "outbox_messages",
                columns: new[] { "processed_at", "retry_count", "lock_lease_expires_at" });
        }
    }
}
