using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecommendationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class UpdateRankingIndexesToDesc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_listing_popularity_ranking",
                schema: "recommendation",
                table: "listing_popularity");

            migrationBuilder.DropIndex(
                name: "idx_user_interest_ranking",
                schema: "recommendation",
                table: "user_equipment_type_interest");

            migrationBuilder.CreateIndex(
                name: "idx_listing_popularity_ranking",
                schema: "recommendation",
                table: "listing_popularity",
                columns: new[] { "score", "equipment_listing_id" },
                descending: new[] { true, true });

            migrationBuilder.CreateIndex(
                name: "idx_user_interest_ranking",
                schema: "recommendation",
                table: "user_equipment_type_interest",
                columns: new[] { "user_id", "score", "equipment_type_id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_listing_popularity_ranking",
                schema: "recommendation",
                table: "listing_popularity");

            migrationBuilder.DropIndex(
                name: "idx_user_interest_ranking",
                schema: "recommendation",
                table: "user_equipment_type_interest");

            migrationBuilder.CreateIndex(
                name: "idx_listing_popularity_ranking",
                schema: "recommendation",
                table: "listing_popularity",
                columns: new[] { "score", "equipment_listing_id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "idx_user_interest_ranking",
                schema: "recommendation",
                table: "user_equipment_type_interest",
                columns: new[] { "user_id", "score", "equipment_type_id" },
                descending: new[] { false, true, false });
        }
    }
}
