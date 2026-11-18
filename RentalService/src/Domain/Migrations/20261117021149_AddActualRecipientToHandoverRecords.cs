using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddActualRecipientToHandoverRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActualRecipientName",
                schema: "rental",
                table: "handover_records",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActualRecipientPhone",
                schema: "rental",
                table: "handover_records",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualRecipientName",
                schema: "rental",
                table: "handover_records");

            migrationBuilder.DropColumn(
                name: "ActualRecipientPhone",
                schema: "rental",
                table: "handover_records");
        }
    }
}
