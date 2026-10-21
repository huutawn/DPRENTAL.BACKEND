using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentPublicationProofToRentalInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KafkaMessageKey",
                schema: "rental",
                table: "rental_inbox_messages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicationProofJson",
                schema: "rental",
                table: "rental_inbox_messages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KafkaMessageKey",
                schema: "rental",
                table: "rental_inbox_messages");

            migrationBuilder.DropColumn(
                name: "PublicationProofJson",
                schema: "rental",
                table: "rental_inbox_messages");
        }
    }
}
