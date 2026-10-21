using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRevisionsAndOperationHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "settlement_ledger_entries",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "rental_item_settlements",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AlterColumn<string>(
                name: "SubAccount",
                table: "payment_transactions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AccountNumber",
                table: "payment_transactions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "payment_transactions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "QuarantineReason",
                table: "outbox_messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QuarantinedAt",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "outbox_messages",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "operation_receipts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "manual_refund_requirements",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "invoice_items",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "operation_headers",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    MemberDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityClaimsDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_headers", x => x.OperationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_QuarantinedAt",
                table: "outbox_messages",
                column: "QuarantinedAt");

            migrationBuilder.CreateIndex(
                name: "IX_operation_headers_CreatedAt",
                table: "operation_headers",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_headers");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_QuarantinedAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "settlement_ledger_entries");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "rental_item_settlements");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "QuarantineReason",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "QuarantinedAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "operation_receipts");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "invoice_items");

            migrationBuilder.AlterColumn<string>(
                name: "SubAccount",
                table: "payment_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AccountNumber",
                table: "payment_transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);
        }
    }
}
