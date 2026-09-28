using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMediaOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_assets_OwnerService_OwnerEntityType_OwnerEntityId",
                schema: "media",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "OwnerEntityId",
                schema: "media",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "OwnerEntityType",
                schema: "media",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "OwnerService",
                schema: "media",
                table: "media_assets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException(
                "Owner values were discarded by this migration. Restore a pre-migration database backup before running the old application.");
        }
    }
}
