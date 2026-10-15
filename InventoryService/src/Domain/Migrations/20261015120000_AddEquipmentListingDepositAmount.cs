using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentListingDepositAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "deposit_amount",
                schema: "inventory",
                table: "equipment_listings",
                type: "numeric(18,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_listings_deposit_amount",
                schema: "inventory",
                table: "equipment_listings",
                sql: "deposit_amount >= 0 AND deposit_amount <= 999999999999999999 AND trunc(deposit_amount) = deposit_amount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM inventory.equipment_listings WHERE deposit_amount <> 0) THEN
                        RAISE EXCEPTION 'Cannot rollback migration: there are equipment listings with non-zero deposit_amount.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_listings_deposit_amount",
                schema: "inventory",
                table: "equipment_listings");

            migrationBuilder.DropColumn(
                name: "deposit_amount",
                schema: "inventory",
                table: "equipment_listings");
        }
    }
}
