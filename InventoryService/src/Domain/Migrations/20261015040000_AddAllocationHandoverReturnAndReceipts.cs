using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAllocationHandoverReturnAndReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "handed_over_at",
                schema: "inventory",
                table: "asset_allocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "returned_at",
                schema: "inventory",
                table: "asset_allocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_asset_allocations_handover_return",
                schema: "inventory",
                table: "asset_allocations",
                sql: "(\"returned_at\" IS NULL OR \"handed_over_at\" IS NOT NULL) AND (\"returned_at\" IS NULL OR \"returned_at\" >= \"handed_over_at\")");

            migrationBuilder.CreateTable(
                name: "allocation_operation_receipts",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    operation_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rental_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    command_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    payload_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    closes_group = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    result_payload_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocation_operation_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allocation_operation_receipts_source_opkey",
                schema: "inventory",
                table: "allocation_operation_receipts",
                columns: new[] { "source_system", "operation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_allocation_operation_receipts_group",
                schema: "inventory",
                table: "allocation_operation_receipts",
                columns: new[] { "source_system", "group_reference" },
                filter: "\"group_reference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_allocation_operation_receipts_order",
                schema: "inventory",
                table: "allocation_operation_receipts",
                column: "rental_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocation_operation_receipts",
                schema: "inventory");

            migrationBuilder.DropCheckConstraint(
                name: "ck_asset_allocations_handover_return",
                schema: "inventory",
                table: "asset_allocations");

            migrationBuilder.DropColumn(
                name: "handed_over_at",
                schema: "inventory",
                table: "asset_allocations");

            migrationBuilder.DropColumn(
                name: "returned_at",
                schema: "inventory",
                table: "asset_allocations");
        }
    }
}
