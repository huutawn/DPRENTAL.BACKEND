using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class UpdateJournalAndOutboxModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalPayload",
                table: "transaction_journal",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "transaction_journal",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "OutboxMessageId",
                table: "transaction_journal",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CancelReason",
                table: "invoices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_OperationId",
                table: "transaction_journal",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_OutboxDigest",
                table: "transaction_journal",
                column: "OutboxDigest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_transaction_journal_OperationId",
                table: "transaction_journal");

            migrationBuilder.DropIndex(
                name: "IX_transaction_journal_OutboxDigest",
                table: "transaction_journal");

            migrationBuilder.DropColumn(
                name: "CanonicalPayload",
                table: "transaction_journal");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "transaction_journal");

            migrationBuilder.DropColumn(
                name: "OutboxMessageId",
                table: "transaction_journal");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "outbox_messages");

            migrationBuilder.AlterColumn<string>(
                name: "CancelReason",
                table: "invoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
