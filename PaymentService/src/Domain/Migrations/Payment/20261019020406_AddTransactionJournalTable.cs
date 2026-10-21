using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionJournalTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transaction_journal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StreamType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfirmationSource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaymentRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    InvoiceRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PreviousHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EncryptedPayload = table.Column<string>(type: "text", nullable: false),
                    PayloadNonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadTag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncryptionKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProofSignature = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SigningKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_transaction_journal", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_EventId",
                table: "transaction_journal",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_InvoiceRef",
                table: "transaction_journal",
                column: "InvoiceRef");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_IsConfirmed_TimestampUtc",
                table: "transaction_journal",
                columns: new[] { "IsConfirmed", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_PaymentRef",
                table: "transaction_journal",
                column: "PaymentRef");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_RentalOrderId",
                table: "transaction_journal",
                column: "RentalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_StreamId",
                table: "transaction_journal",
                column: "StreamId");

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_StreamId_Version",
                table: "transaction_journal",
                columns: new[] { "StreamId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_transaction_journal_TimestampUtc_Id",
                table: "transaction_journal",
                columns: new[] { "TimestampUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transaction_journal");
        }
    }
}
