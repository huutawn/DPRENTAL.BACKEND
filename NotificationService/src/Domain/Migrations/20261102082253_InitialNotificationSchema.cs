using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitialNotificationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notification");

            migrationBuilder.CreateTable(
                name: "aggregate_watermarks",
                schema: "notification",
                columns: table => new
                {
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastOrderVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastMessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsTerminal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    TerminalState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_aggregate_watermarks", x => new { x.AggregateId, x.RecipientUserId });
                });

            migrationBuilder.CreateTable(
                name: "inbox_receipts",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Producer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorDetail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_receipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceProducer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceEventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TemplateCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RenderDataJson = table.Column<string>(type: "text", nullable: true),
                    DeepLinkUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsActionable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ActionUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "preferences",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "delivery_jobs",
                schema: "notification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    LeaseToken = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorDetail = table.Column<string>(type: "text", nullable: true),
                    DispatchStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_jobs_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalSchema: "notification",
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_jobs_lease_recovery",
                schema: "notification",
                table: "delivery_jobs",
                columns: new[] { "Status", "LeaseExpiresAt" },
                filter: "\"Status\" = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "ix_delivery_jobs_pending_claim",
                schema: "notification",
                table: "delivery_jobs",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "\"Status\" IN ('Pending', 'RetryScheduled')");

            migrationBuilder.CreateIndex(
                name: "uq_delivery_jobs_notification_channel",
                schema: "notification",
                table: "delivery_jobs",
                columns: new[] { "NotificationId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_inbox_receipts_processed_at",
                schema: "notification",
                table: "inbox_receipts",
                column: "ProcessedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "uq_inbox_receipts_consumer_message",
                schema: "notification",
                table: "inbox_receipts",
                columns: new[] { "ConsumerName", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_created",
                schema: "notification",
                table: "notifications",
                columns: new[] { "RecipientUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_unread",
                schema: "notification",
                table: "notifications",
                columns: new[] { "RecipientUserId", "ReadAt" },
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "uq_notifications_source_recipient_type",
                schema: "notification",
                table: "notifications",
                columns: new[] { "SourceEventId", "RecipientUserId", "NotificationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_preferences_user_id",
                schema: "notification",
                table: "preferences",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "uq_preferences_user_category",
                schema: "notification",
                table: "preferences",
                columns: new[] { "UserId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "aggregate_watermarks",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "delivery_jobs",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "inbox_receipts",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "preferences",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notification");
        }
    }
}
