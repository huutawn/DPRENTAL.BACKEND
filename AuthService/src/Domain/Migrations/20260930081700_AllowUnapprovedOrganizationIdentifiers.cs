using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AllowUnapprovedOrganizationIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Organizations_Slug",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Slug",
                schema: "auth",
                table: "Organizations",
                column: "Slug",
                unique: true,
                filter: "\"Status\" IN (1, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations",
                column: "TaxCode",
                unique: true,
                filter: "\"Status\" IN (1, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Organizations_Slug",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Slug",
                schema: "auth",
                table: "Organizations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations",
                column: "TaxCode",
                unique: true);
        }
    }
}
