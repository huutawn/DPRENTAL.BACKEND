using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReviewService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCursorToReconciliationCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cursor",
                table: "reconciliation_checkpoints",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cursor",
                table: "reconciliation_checkpoints");
        }
    }
}
