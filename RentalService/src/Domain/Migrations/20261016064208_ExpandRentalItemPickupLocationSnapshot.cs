using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ExpandRentalItemPickupLocationSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PickupLocationSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM rental.rental_items WHERE length("PickupLocationSnapshot") > 500) THEN
                        RAISE EXCEPTION 'Cannot rollback migration: there are rental items with PickupLocationSnapshot longer than 500 characters.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "PickupLocationSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024,
                oldNullable: true);
        }
    }
}
