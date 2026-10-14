using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalSubOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rental_sub_orders",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubOrderCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_sub_orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rental_sub_orders_rental_orders_RentalOrderId",
                        column: x => x.RentalOrderId,
                        principalSchema: "rental",
                        principalTable: "rental_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Thêm cột cho phép trống, tạo đơn con cho các đơn đã có (mỗi nhà cung cấp một đơn con,
            // đánh số theo SupplierId), gắn từng dòng vào đơn con rồi mới bắt buộc cột.
            migrationBuilder.AddColumn<Guid>(
                name: "RentalSubOrderId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                INSERT INTO rental.rental_sub_orders ("Id", "RentalOrderId", "SupplierId", "SubOrderCode", "CreatedAt")
                SELECT gen_random_uuid(),
                       g."RentalOrderId",
                       g."SupplierId",
                       o."OrderCode" || '-' || lpad(g.seq::text, 2, '0'),
                       o."CreatedAt"
                FROM (
                    SELECT "RentalOrderId",
                           "SupplierId",
                           row_number() OVER (PARTITION BY "RentalOrderId" ORDER BY "SupplierId") AS seq
                    FROM rental.rental_items
                    GROUP BY "RentalOrderId", "SupplierId"
                ) g
                JOIN rental.rental_orders o ON o."Id" = g."RentalOrderId";

                UPDATE rental.rental_items i
                SET "RentalSubOrderId" = s."Id"
                FROM rental.rental_sub_orders s
                WHERE s."RentalOrderId" = i."RentalOrderId" AND s."SupplierId" = i."SupplierId";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RentalSubOrderId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_items_RentalSubOrderId",
                schema: "rental",
                table: "rental_items",
                column: "RentalSubOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_sub_orders_RentalOrderId_SupplierId",
                schema: "rental",
                table: "rental_sub_orders",
                columns: new[] { "RentalOrderId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_sub_orders_SubOrderCode",
                schema: "rental",
                table: "rental_sub_orders",
                column: "SubOrderCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_sub_orders_SupplierId_CreatedAt",
                schema: "rental",
                table: "rental_sub_orders",
                columns: new[] { "SupplierId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_rental_items_rental_sub_orders_RentalSubOrderId",
                schema: "rental",
                table: "rental_items",
                column: "RentalSubOrderId",
                principalSchema: "rental",
                principalTable: "rental_sub_orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_rental_items_rental_sub_orders_RentalSubOrderId",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropTable(
                name: "rental_sub_orders",
                schema: "rental");

            migrationBuilder.DropIndex(
                name: "IX_rental_items_RentalSubOrderId",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "RentalSubOrderId",
                schema: "rental",
                table: "rental_items");
        }
    }
}
