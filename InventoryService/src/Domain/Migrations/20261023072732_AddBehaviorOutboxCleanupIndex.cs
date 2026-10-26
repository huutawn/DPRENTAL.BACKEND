using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBehaviorOutboxCleanupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_behavior_cleanup",
                schema: "inventory",
                table: "outbox_messages",
                columns: new[] { "published_at", "id" },
                filter: "topic = 'inventory.behavior.events.v1' AND published_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_behavior_cleanup",
                schema: "inventory",
                table: "outbox_messages");
        }
    }
}
