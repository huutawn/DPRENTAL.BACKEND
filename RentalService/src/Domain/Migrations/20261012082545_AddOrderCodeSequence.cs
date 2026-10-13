using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCodeSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_rental_cart_items_rental_carts_RentalCartId",
                schema: "rental",
                table: "rental_cart_items");

            migrationBuilder.CreateSequence(
                name: "order_code_seq",
                schema: "rental");

            migrationBuilder.AddForeignKey(
                name: "FK_rental_cart_items_rental_carts_RentalCartId",
                schema: "rental",
                table: "rental_cart_items",
                column: "RentalCartId",
                principalSchema: "rental",
                principalTable: "rental_carts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_rental_cart_items_rental_carts_RentalCartId",
                schema: "rental",
                table: "rental_cart_items");

            migrationBuilder.DropSequence(
                name: "order_code_seq",
                schema: "rental");

            migrationBuilder.AddForeignKey(
                name: "FK_rental_cart_items_rental_carts_RentalCartId",
                schema: "rental",
                table: "rental_cart_items",
                column: "RentalCartId",
                principalSchema: "rental",
                principalTable: "rental_carts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
