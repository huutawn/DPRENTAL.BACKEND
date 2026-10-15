using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxPublisherMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastErrorMessage",
                table: "outbox_messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedBy",
                table: "outbox_messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntilUtc",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_PublishedAt_LockedUntilUtc_NextRetryAt_Retr~",
                table: "outbox_messages",
                columns: new[] { "PublishedAt", "LockedUntilUtc", "NextRetryAt", "RetryCount" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_PublishedAt_LockedUntilUtc_NextRetryAt_Retr~",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastErrorMessage",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "outbox_messages");
        }
    }
}
