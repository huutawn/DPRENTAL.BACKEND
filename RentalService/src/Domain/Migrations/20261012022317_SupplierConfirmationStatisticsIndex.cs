using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861
namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class SupplierConfirmationStatisticsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_rental_state_transitions_RentalItemId_Action_OccurredAt",
                schema: "rental",
                table: "rental_state_transitions",
                columns: new[] { "RentalItemId", "Action", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rental_state_transitions_RentalItemId_Action_OccurredAt",
                schema: "rental",
                table: "rental_state_transitions");
        }
    }
}
