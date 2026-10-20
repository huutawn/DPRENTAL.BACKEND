using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanupClaimLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByWorkerId",
                schema: "media",
                table: "media_cleanup_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_cleanup_items_Status_EligibleAtUtc_LeaseExpiresAtUtc",
                schema: "media",
                table: "media_cleanup_items",
                columns: new[] { "Status", "EligibleAtUtc", "LeaseExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_cleanup_items_Status_EligibleAtUtc_LeaseExpiresAtUtc",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.DropColumn(
                name: "ClaimedByWorkerId",
                schema: "media",
                table: "media_cleanup_items");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                schema: "media",
                table: "media_cleanup_items");
        }
    }
}
