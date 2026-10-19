using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class UpdateJournalEnvelopeDigestAndRemoveCanonicalPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanonicalPayload",
                table: "transaction_journal");

            migrationBuilder.AddColumn<string>(
                name: "EnvelopeDigest",
                table: "transaction_journal",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvelopeDigest",
                table: "transaction_journal");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalPayload",
                table: "transaction_journal",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
