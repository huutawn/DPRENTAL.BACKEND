using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddInvoiceCancellationOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancellationOrigin",
                table: "invoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationOrigin",
                table: "invoices");
        }
    }
}
