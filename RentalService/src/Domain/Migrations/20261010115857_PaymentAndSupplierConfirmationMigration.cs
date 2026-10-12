using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861
namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class PaymentAndSupplierConfirmationMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiResponseJson",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorClassification",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTransient",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOutcomeUncertain",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptedAt",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrerequisiteOperationId",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseJson",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefundCompletionJson",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Succeeded");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetItemId",
                schema: "rental",
                table: "rental_operation_receipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierDecisionBy",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierDecisionNote",
                schema: "rental",
                table: "rental_items",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rental_inbox_messages",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Producer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AggregateId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CausationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    PayloadFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    KafkaTopic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    KafkaPartition = table.Column<int>(type: "integer", nullable: true),
                    KafkaOffset = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_inbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rental_outbox_messages",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PartitionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rental_operation_receipts_Action_Status",
                schema: "rental",
                table: "rental_operation_receipts",
                columns: new[] { "Action", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_operation_receipts_PrerequisiteOperationId",
                schema: "rental",
                table: "rental_operation_receipts",
                column: "PrerequisiteOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_operation_receipts_Status_NextRetryAt",
                schema: "rental",
                table: "rental_operation_receipts",
                columns: new[] { "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_operation_receipts_TargetItemId",
                schema: "rental",
                table: "rental_operation_receipts",
                column: "TargetItemId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_items_SupplierDecisionBy",
                schema: "rental",
                table: "rental_items",
                column: "SupplierDecisionBy");

            migrationBuilder.CreateIndex(
                name: "IX_rental_inbox_messages_MessageId",
                schema: "rental",
                table: "rental_inbox_messages",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_inbox_messages_Status",
                schema: "rental",
                table: "rental_inbox_messages",
                column: "Status");


            migrationBuilder.CreateIndex(
                name: "IX_rental_inbox_messages_Status_NextRetryAt_ReceivedAt",
                schema: "rental",
                table: "rental_inbox_messages",
                columns: new[] { "Status", "NextRetryAt", "ReceivedAt" });
            migrationBuilder.CreateIndex(
                name: "IX_rental_outbox_messages_Status",
                schema: "rental",
                table: "rental_outbox_messages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_rental_outbox_messages_Status_ScheduledAt_CreatedAt",
                schema: "rental",
                table: "rental_outbox_messages",
                columns: new[] { "Status", "ScheduledAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rental_outbox_messages_Status_ScheduledAt_LockedUntilUtc",
                schema: "rental",
                table: "rental_outbox_messages",
                columns: new[] { "Status", "ScheduledAt", "LockedUntilUtc" });
            migrationBuilder.Sql("UPDATE rental.rental_operation_receipts SET \"Status\" = CASE WHEN \"IsSuccess\" THEN 'Succeeded' ELSE 'Failed' END;");
            migrationBuilder.Sql("UPDATE rental.rental_orders SET \"Status\" = 9 WHERE \"Status\" = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rental_inbox_messages",
                schema: "rental");

            migrationBuilder.DropTable(
                name: "rental_outbox_messages",
                schema: "rental");

            migrationBuilder.DropIndex(
                name: "IX_rental_operation_receipts_Action_Status",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_rental_operation_receipts_PrerequisiteOperationId",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_rental_operation_receipts_Status_NextRetryAt",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_rental_operation_receipts_TargetItemId",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropIndex(
                name: "IX_rental_items_SupplierDecisionBy",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "ApiResponseJson",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "ErrorClassification",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "IsTransient",
                schema: "rental",
                table: "rental_operation_receipts");


            migrationBuilder.DropColumn(
                name: "IsOutcomeUncertain",
                schema: "rental",
                table: "rental_operation_receipts");
            migrationBuilder.DropColumn(
                name: "LastAttemptedAt",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "PrerequisiteOperationId",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "ResponseJson",
                schema: "rental",
                table: "rental_operation_receipts");


            migrationBuilder.DropColumn(
                name: "RefundCompletionJson",
                schema: "rental",
                table: "rental_operation_receipts");
            migrationBuilder.DropColumn(
                name: "RetryCount",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "TargetItemId",
                schema: "rental",
                table: "rental_operation_receipts");

            migrationBuilder.DropColumn(
                name: "SupplierDecisionBy",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "SupplierDecisionNote",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.Sql("UPDATE rental.rental_orders SET \"Status\" = 1 WHERE \"Status\" = 9;");
        }
    }
}
