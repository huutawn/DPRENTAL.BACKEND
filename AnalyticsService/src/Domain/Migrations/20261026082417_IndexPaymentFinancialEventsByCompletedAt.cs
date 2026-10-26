using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration uses inline arrays for column lists.
#nullable disable

namespace AnalyticsService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class IndexPaymentFinancialEventsByCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_payment_financial_events_MessageType_CompletedAt_Currency",
                table: "payment_financial_events",
                columns: new[] { "MessageType", "CompletedAt", "Currency" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_financial_events_MessageType_CompletedAt_Currency",
                table: "payment_financial_events");
        }
    }
}
#pragma warning restore CA1861
