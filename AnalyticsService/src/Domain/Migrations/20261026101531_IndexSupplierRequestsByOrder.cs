using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration index column array.
#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class IndexSupplierRequestsByOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_analytics_rental_items_SupplierId_RentalOrderId",
                table: "analytics_rental_items",
                columns: new[] { "SupplierId", "RentalOrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_analytics_rental_items_SupplierId_RentalOrderId",
                table: "analytics_rental_items");
        }
    }
}
#pragma warning restore CA1861
