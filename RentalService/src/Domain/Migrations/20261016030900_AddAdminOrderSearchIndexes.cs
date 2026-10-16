using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <summary>
    /// Index cho Admin tìm đơn theo thiết bị và theo máy cụ thể. Chỉ thêm index, không đổi dữ liệu; rollback xóa index.
    /// </summary>
    /// <remarks>
    /// Model snapshot đi kèm cũng bổ sung bảng rental_checkout_operations, sequence và cột Sequence của outbox mà
    /// migration viết tay AddOutboxSequenceAndCheckoutOperation đã tạo nhưng chưa ghi vào snapshot. Các thay đổi đó
    /// không lặp lại ở đây.
    /// </remarks>
    public partial class AddAdminOrderSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_rental_unit_assignments_AssetId",
                schema: "rental",
                table: "rental_unit_assignments",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_rental_items_EquipmentListingId",
                schema: "rental",
                table: "rental_items",
                column: "EquipmentListingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rental_unit_assignments_AssetId",
                schema: "rental",
                table: "rental_unit_assignments");

            migrationBuilder.DropIndex(
                name: "IX_rental_items_EquipmentListingId",
                schema: "rental",
                table: "rental_items");
        }
    }
}
