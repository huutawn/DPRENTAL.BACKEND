using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSchemaUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_jobs_notifications_NotificationId",
                schema: "notification",
                table: "delivery_jobs");

            migrationBuilder.RenameColumn(
                name: "Category",
                schema: "notification",
                table: "preferences",
                newName: "category");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notification",
                table: "preferences",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "notification",
                table: "preferences",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "notification",
                table: "preferences",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "EmailEnabled",
                schema: "notification",
                table: "preferences",
                newName: "email_enabled");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "notification",
                table: "preferences",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "Title",
                schema: "notification",
                table: "notifications",
                newName: "title");

            migrationBuilder.RenameColumn(
                name: "Severity",
                schema: "notification",
                table: "notifications",
                newName: "severity");

            migrationBuilder.RenameColumn(
                name: "Category",
                schema: "notification",
                table: "notifications",
                newName: "category");

            migrationBuilder.RenameColumn(
                name: "Body",
                schema: "notification",
                table: "notifications",
                newName: "body");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notification",
                table: "notifications",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TemplateVersion",
                schema: "notification",
                table: "notifications",
                newName: "template_version");

            migrationBuilder.RenameColumn(
                name: "TemplateCode",
                schema: "notification",
                table: "notifications",
                newName: "template_code");

            migrationBuilder.RenameColumn(
                name: "SourceProducer",
                schema: "notification",
                table: "notifications",
                newName: "source_producer");

            migrationBuilder.RenameColumn(
                name: "SourceEventType",
                schema: "notification",
                table: "notifications",
                newName: "source_event_type");

            migrationBuilder.RenameColumn(
                name: "SourceEventId",
                schema: "notification",
                table: "notifications",
                newName: "source_event_id");

            migrationBuilder.RenameColumn(
                name: "ResourceType",
                schema: "notification",
                table: "notifications",
                newName: "resource_type");

            migrationBuilder.RenameColumn(
                name: "ResourceId",
                schema: "notification",
                table: "notifications",
                newName: "resource_id");

            migrationBuilder.RenameColumn(
                name: "RenderDataJson",
                schema: "notification",
                table: "notifications",
                newName: "render_data_json");

            migrationBuilder.RenameColumn(
                name: "RecipientUserId",
                schema: "notification",
                table: "notifications",
                newName: "recipient_user_id");

            migrationBuilder.RenameColumn(
                name: "ReadAt",
                schema: "notification",
                table: "notifications",
                newName: "read_at");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "notification",
                table: "notifications",
                newName: "occurred_at");

            migrationBuilder.RenameColumn(
                name: "NotificationType",
                schema: "notification",
                table: "notifications",
                newName: "notification_type");

            migrationBuilder.RenameColumn(
                name: "IsActionable",
                schema: "notification",
                table: "notifications",
                newName: "is_actionable");

            migrationBuilder.RenameColumn(
                name: "EventVersion",
                schema: "notification",
                table: "notifications",
                newName: "event_version");

            migrationBuilder.RenameColumn(
                name: "DeepLinkUrl",
                schema: "notification",
                table: "notifications",
                newName: "deep_link_url");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "notification",
                table: "notifications",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "ActionUrl",
                schema: "notification",
                table: "notifications",
                newName: "action_url");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "notification",
                table: "inbox_receipts",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Producer",
                schema: "notification",
                table: "inbox_receipts",
                newName: "producer");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notification",
                table: "inbox_receipts",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "notification",
                table: "inbox_receipts",
                newName: "processed_at");

            migrationBuilder.RenameColumn(
                name: "MessageType",
                schema: "notification",
                table: "inbox_receipts",
                newName: "message_type");

            migrationBuilder.RenameColumn(
                name: "MessageId",
                schema: "notification",
                table: "inbox_receipts",
                newName: "message_id");

            migrationBuilder.RenameColumn(
                name: "ErrorDetail",
                schema: "notification",
                table: "inbox_receipts",
                newName: "error_detail");

            migrationBuilder.RenameColumn(
                name: "ErrorCode",
                schema: "notification",
                table: "inbox_receipts",
                newName: "error_code");

            migrationBuilder.RenameColumn(
                name: "ConsumerName",
                schema: "notification",
                table: "inbox_receipts",
                newName: "consumer_name");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "notification",
                table: "delivery_jobs",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Channel",
                schema: "notification",
                table: "delivery_jobs",
                newName: "channel");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notification",
                table: "delivery_jobs",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "SentAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "sent_at");

            migrationBuilder.RenameColumn(
                name: "RecipientUserId",
                schema: "notification",
                table: "delivery_jobs",
                newName: "recipient_user_id");

            migrationBuilder.RenameColumn(
                name: "ProviderMessageId",
                schema: "notification",
                table: "delivery_jobs",
                newName: "provider_message_id");

            migrationBuilder.RenameColumn(
                name: "NotificationId",
                schema: "notification",
                table: "delivery_jobs",
                newName: "notification_id");

            migrationBuilder.RenameColumn(
                name: "NextAttemptAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "next_attempt_at");

            migrationBuilder.RenameColumn(
                name: "MaxAttempts",
                schema: "notification",
                table: "delivery_jobs",
                newName: "max_attempts");

            migrationBuilder.RenameColumn(
                name: "LeaseToken",
                schema: "notification",
                table: "delivery_jobs",
                newName: "lease_token");

            migrationBuilder.RenameColumn(
                name: "LeaseExpiresAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "lease_expires_at");

            migrationBuilder.RenameColumn(
                name: "LastErrorDetail",
                schema: "notification",
                table: "delivery_jobs",
                newName: "last_error_detail");

            migrationBuilder.RenameColumn(
                name: "LastErrorCode",
                schema: "notification",
                table: "delivery_jobs",
                newName: "last_error_code");

            migrationBuilder.RenameColumn(
                name: "DispatchStartedAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "dispatch_started_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "notification",
                table: "delivery_jobs",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AttemptCount",
                schema: "notification",
                table: "delivery_jobs",
                newName: "attempt_count");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TerminalState",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "terminal_state");

            migrationBuilder.RenameColumn(
                name: "LastOrderVersion",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "last_order_version");

            migrationBuilder.RenameColumn(
                name: "LastMessageId",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "last_message_id");

            migrationBuilder.RenameColumn(
                name: "IsTerminal",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "is_terminal");

            migrationBuilder.RenameColumn(
                name: "RecipientUserId",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "recipient_user_id");

            migrationBuilder.RenameColumn(
                name: "AggregateId",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "aggregate_id");

            migrationBuilder.DropPrimaryKey(
                name: "PK_aggregate_watermarks",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.AddColumn<string>(
                name: "scope",
                schema: "notification",
                table: "aggregate_watermarks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "RentalOrder");

            migrationBuilder.AddColumn<bool>(
                name: "is_paid",
                schema: "notification",
                table: "aggregate_watermarks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "parent_order_id",
                schema: "notification",
                table: "aggregate_watermarks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_aggregate_watermarks",
                schema: "notification",
                table: "aggregate_watermarks",
                columns: new[] { "scope", "aggregate_id", "recipient_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_watermarks_scope_aggregate_recipient",
                schema: "notification",
                table: "aggregate_watermarks",
                columns: new[] { "scope", "aggregate_id", "recipient_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_watermarks_scope_aggregate",
                schema: "notification",
                table: "aggregate_watermarks",
                columns: new[] { "scope", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_aggregate_watermarks_parent_order",
                schema: "notification",
                table: "aggregate_watermarks",
                column: "parent_order_id");

            migrationBuilder.AddColumn<Guid>(
                name: "order_id",
                schema: "notification",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_order_id",
                schema: "notification",
                table: "notifications",
                column: "order_id");

            migrationBuilder.Sql(@"
UPDATE notification.aggregate_watermarks w
SET is_paid = true
WHERE w.terminal_state = 'PAID'
   OR EXISTS (
       SELECT 1 FROM notification.notifications n
       WHERE n.notification_type = 'RENTAL_ORDER_PAID'
         AND n.resource_id = w.aggregate_id::text
   );
");

            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET order_id = resource_id::uuid
WHERE resource_type = 'RentalOrder'
  AND order_id IS NULL
  AND resource_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';
");
            migrationBuilder.AddForeignKey(
                name: "FK_delivery_jobs_notifications_notification_id",
                schema: "notification",
                table: "delivery_jobs",
                column: "notification_id",
                principalSchema: "notification",
                principalTable: "notifications",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_jobs_notifications_notification_id",
                schema: "notification",
                table: "delivery_jobs");

            migrationBuilder.DropIndex(
                name: "ix_notifications_order_id",
                schema: "notification",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_aggregate_watermarks_parent_order",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropColumn(
                name: "order_id",
                schema: "notification",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "parent_order_id",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.RenameColumn(
                name: "category",
                schema: "notification",
                table: "preferences",
                newName: "Category");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notification",
                table: "preferences",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "notification",
                table: "preferences",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "notification",
                table: "preferences",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "email_enabled",
                schema: "notification",
                table: "preferences",
                newName: "EmailEnabled");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "notification",
                table: "preferences",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "title",
                schema: "notification",
                table: "notifications",
                newName: "Title");

            migrationBuilder.RenameColumn(
                name: "severity",
                schema: "notification",
                table: "notifications",
                newName: "Severity");

            migrationBuilder.RenameColumn(
                name: "category",
                schema: "notification",
                table: "notifications",
                newName: "Category");

            migrationBuilder.RenameColumn(
                name: "body",
                schema: "notification",
                table: "notifications",
                newName: "Body");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notification",
                table: "notifications",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "template_version",
                schema: "notification",
                table: "notifications",
                newName: "TemplateVersion");

            migrationBuilder.RenameColumn(
                name: "template_code",
                schema: "notification",
                table: "notifications",
                newName: "TemplateCode");

            migrationBuilder.RenameColumn(
                name: "source_producer",
                schema: "notification",
                table: "notifications",
                newName: "SourceProducer");

            migrationBuilder.RenameColumn(
                name: "source_event_type",
                schema: "notification",
                table: "notifications",
                newName: "SourceEventType");

            migrationBuilder.RenameColumn(
                name: "source_event_id",
                schema: "notification",
                table: "notifications",
                newName: "SourceEventId");

            migrationBuilder.RenameColumn(
                name: "resource_type",
                schema: "notification",
                table: "notifications",
                newName: "ResourceType");

            migrationBuilder.RenameColumn(
                name: "resource_id",
                schema: "notification",
                table: "notifications",
                newName: "ResourceId");

            migrationBuilder.RenameColumn(
                name: "render_data_json",
                schema: "notification",
                table: "notifications",
                newName: "RenderDataJson");

            migrationBuilder.RenameColumn(
                name: "recipient_user_id",
                schema: "notification",
                table: "notifications",
                newName: "RecipientUserId");

            migrationBuilder.RenameColumn(
                name: "read_at",
                schema: "notification",
                table: "notifications",
                newName: "ReadAt");

            migrationBuilder.RenameColumn(
                name: "occurred_at",
                schema: "notification",
                table: "notifications",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "notification_type",
                schema: "notification",
                table: "notifications",
                newName: "NotificationType");

            migrationBuilder.RenameColumn(
                name: "is_actionable",
                schema: "notification",
                table: "notifications",
                newName: "IsActionable");

            migrationBuilder.RenameColumn(
                name: "event_version",
                schema: "notification",
                table: "notifications",
                newName: "EventVersion");

            migrationBuilder.RenameColumn(
                name: "deep_link_url",
                schema: "notification",
                table: "notifications",
                newName: "DeepLinkUrl");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "notification",
                table: "notifications",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "action_url",
                schema: "notification",
                table: "notifications",
                newName: "ActionUrl");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "notification",
                table: "inbox_receipts",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "producer",
                schema: "notification",
                table: "inbox_receipts",
                newName: "Producer");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notification",
                table: "inbox_receipts",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "processed_at",
                schema: "notification",
                table: "inbox_receipts",
                newName: "ProcessedAt");

            migrationBuilder.RenameColumn(
                name: "message_type",
                schema: "notification",
                table: "inbox_receipts",
                newName: "MessageType");

            migrationBuilder.RenameColumn(
                name: "message_id",
                schema: "notification",
                table: "inbox_receipts",
                newName: "MessageId");

            migrationBuilder.RenameColumn(
                name: "error_detail",
                schema: "notification",
                table: "inbox_receipts",
                newName: "ErrorDetail");

            migrationBuilder.RenameColumn(
                name: "error_code",
                schema: "notification",
                table: "inbox_receipts",
                newName: "ErrorCode");

            migrationBuilder.RenameColumn(
                name: "consumer_name",
                schema: "notification",
                table: "inbox_receipts",
                newName: "ConsumerName");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "notification",
                table: "delivery_jobs",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "channel",
                schema: "notification",
                table: "delivery_jobs",
                newName: "Channel");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notification",
                table: "delivery_jobs",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "sent_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "SentAt");

            migrationBuilder.RenameColumn(
                name: "recipient_user_id",
                schema: "notification",
                table: "delivery_jobs",
                newName: "RecipientUserId");

            migrationBuilder.RenameColumn(
                name: "provider_message_id",
                schema: "notification",
                table: "delivery_jobs",
                newName: "ProviderMessageId");

            migrationBuilder.RenameColumn(
                name: "notification_id",
                schema: "notification",
                table: "delivery_jobs",
                newName: "NotificationId");

            migrationBuilder.RenameColumn(
                name: "next_attempt_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "NextAttemptAt");

            migrationBuilder.RenameColumn(
                name: "max_attempts",
                schema: "notification",
                table: "delivery_jobs",
                newName: "MaxAttempts");

            migrationBuilder.RenameColumn(
                name: "lease_token",
                schema: "notification",
                table: "delivery_jobs",
                newName: "LeaseToken");

            migrationBuilder.RenameColumn(
                name: "lease_expires_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "LeaseExpiresAt");

            migrationBuilder.RenameColumn(
                name: "last_error_detail",
                schema: "notification",
                table: "delivery_jobs",
                newName: "LastErrorDetail");

            migrationBuilder.RenameColumn(
                name: "last_error_code",
                schema: "notification",
                table: "delivery_jobs",
                newName: "LastErrorCode");

            migrationBuilder.RenameColumn(
                name: "dispatch_started_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "DispatchStartedAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "notification",
                table: "delivery_jobs",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "attempt_count",
                schema: "notification",
                table: "delivery_jobs",
                newName: "AttemptCount");

            migrationBuilder.DropIndex(
                name: "ix_aggregate_watermarks_parent_order",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropIndex(
                name: "ix_aggregate_watermarks_scope_aggregate",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropIndex(
                name: "ix_aggregate_watermarks_scope_aggregate_recipient",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_aggregate_watermarks",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropColumn(
                name: "parent_order_id",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropColumn(
                name: "is_paid",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.DropColumn(
                name: "scope",
                schema: "notification",
                table: "aggregate_watermarks");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "terminal_state",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "TerminalState");

            migrationBuilder.RenameColumn(
                name: "last_order_version",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "LastOrderVersion");

            migrationBuilder.RenameColumn(
                name: "last_message_id",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "LastMessageId");

            migrationBuilder.RenameColumn(
                name: "is_terminal",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "IsTerminal");

            migrationBuilder.RenameColumn(
                name: "recipient_user_id",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "RecipientUserId");

            migrationBuilder.RenameColumn(
                name: "aggregate_id",
                schema: "notification",
                table: "aggregate_watermarks",
                newName: "AggregateId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_aggregate_watermarks",
                schema: "notification",
                table: "aggregate_watermarks",
                columns: new[] { "AggregateId", "RecipientUserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_jobs_notifications_NotificationId",
                schema: "notification",
                table: "delivery_jobs",
                column: "NotificationId",
                principalSchema: "notification",
                principalTable: "notifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
