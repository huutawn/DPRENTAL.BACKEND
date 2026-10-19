using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.IntegrityHost.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityHeadRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LatestRevision",
                table: "integrity_entity_heads",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestRevision",
                table: "integrity_entity_heads");
        }
    }
}
