#nullable disable
#pragma warning disable CA1861

using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dprental.RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxSequenceAndCheckoutOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence<long>(
                name: "rental_outbox_sequence",
                schema: "rental");

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                schema: "rental",
                table: "rental_outbox_messages",
                type: "bigint",
                nullable: false,
                defaultValueSql: "nextval('rental.rental_outbox_sequence')");

            // Backfill Sequence cho các outbox message cũ theo (CreatedAt, Id)
            migrationBuilder.Sql(@"
                UPDATE rental.rental_outbox_messages SET ""Sequence"" = sub.rn
                FROM (SELECT ""Id"", row_number() OVER (ORDER BY ""CreatedAt"", ""Id"") AS rn FROM rental.rental_outbox_messages) sub
                WHERE rental.rental_outbox_messages.""Id"" = sub.""Id"";
                SELECT setval('rental.rental_outbox_sequence', COALESCE((SELECT MAX(""Sequence"") FROM rental.rental_outbox_messages), 0) + 1, false);
            ");

            migrationBuilder.CreateIndex(
                name: "IX_rental_outbox_messages_Topic_PartitionKey_Sequence_Status",
                schema: "rental",
                table: "rental_outbox_messages",
                columns: new[] { "Topic", "PartitionKey", "Sequence", "Status" });

            migrationBuilder.CreateTable(
                name: "rental_checkout_operations",
                schema: "rental",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RenterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpectedRentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PreparationSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    ItemHoldStatusesJson = table.Column<string>(type: "text", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InvoiceStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_checkout_operations", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rental_checkout_operations_RenterUserId",
                schema: "rental",
                table: "rental_checkout_operations",
                column: "RenterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_checkout_operations_RenterUserId_Status",
                schema: "rental",
                table: "rental_checkout_operations",
                columns: new[] { "RenterUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rental_checkout_operations",
                schema: "rental");

            migrationBuilder.DropIndex(
                name: "IX_rental_outbox_messages_Topic_PartitionKey_Sequence_Status",
                schema: "rental",
                table: "rental_outbox_messages");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "rental",
                table: "rental_outbox_messages");

            migrationBuilder.DropSequence(
                name: "rental_outbox_sequence",
                schema: "rental");
        }
    }
}
