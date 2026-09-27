using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleMediaCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_cleanup_items_Status_RetryCount",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EligibleAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                type: "timestamp with time zone",
                nullable: true);

            // Existing jobs may still target a PUT URL that has not expired.
            migrationBuilder.Sql("UPDATE media.media_cleanup_items SET \"EligibleAtUtc\" = \"CreatedAtUtc\" + interval '16 minutes'");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "EligibleAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_cleanup_items_Status_EligibleAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                columns: new[] { "Status", "EligibleAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_cleanup_items_Status_EligibleAtUtc",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.DropColumn(
                name: "EligibleAtUtc",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.CreateIndex(
                name: "IX_media_cleanup_items_Status_RetryCount",
                schema: "media",
                table: "media_cleanup_items",
                columns: new[] { "Status", "RetryCount" });
        }
    }
}
