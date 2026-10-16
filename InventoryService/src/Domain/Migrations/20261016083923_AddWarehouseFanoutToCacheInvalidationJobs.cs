using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddWarehouseFanoutToCacheInvalidationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "fanout_after_listing_id",
                schema: "inventory",
                table: "cache_invalidation_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "fanout_owner_user_id",
                schema: "inventory",
                table: "cache_invalidation_jobs",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fanout_after_listing_id",
                schema: "inventory",
                table: "cache_invalidation_jobs");

            migrationBuilder.DropColumn(
                name: "fanout_owner_user_id",
                schema: "inventory",
                table: "cache_invalidation_jobs");
        }
    }
}
