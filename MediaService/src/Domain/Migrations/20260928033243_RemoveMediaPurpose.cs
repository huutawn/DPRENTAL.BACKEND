using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMediaPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_assets_Purpose",
                schema: "media",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "Purpose",
                schema: "media",
                table: "media_assets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "Purpose data was removed. Restore a database backup before running the previous MediaService version.");
        }
    }
}
