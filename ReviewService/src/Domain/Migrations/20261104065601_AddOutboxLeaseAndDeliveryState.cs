using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReviewService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLeaseAndDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "claim_owner",
                table: "outbox_messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "claim_token",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_state",
                table: "outbox_messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "last_error_code",
                table: "outbox_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_outbox_claim",
                table: "outbox_messages",
                columns: new[] { "delivery_state", "next_attempt_at", "lease_expires_at" });

            migrationBuilder.Sql("UPDATE outbox_messages SET delivery_state = 'Published' WHERE processed_at IS NOT NULL;");
            migrationBuilder.Sql("UPDATE outbox_messages SET delivery_state = 'Pending' WHERE processed_at IS NULL;");
            migrationBuilder.Sql("UPDATE outbox_messages SET attempts = 0 WHERE attempts IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_outbox_claim",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claim_owner",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claim_token",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "delivery_state",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "last_error_code",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "outbox_messages");
        }
    }
}
