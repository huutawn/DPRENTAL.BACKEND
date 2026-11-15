using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814, CA1861 // Migration do EF sinh: mảng đa chiều/hằng mảng

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddDamageLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DamageLevelsSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "damage_levels",
                schema: "rental",
                columns: table => new
                {
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Percent = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_damage_levels", x => x.Level);
                });

            migrationBuilder.InsertData(
                schema: "rental",
                table: "damage_levels",
                columns: new[] { "Level", "Description", "Name", "Percent" },
                values: new object[,]
                {
                    { 0, "Máy nguyên vẹn, không khấu trừ.", "Không hư", 0 },
                    { 1, "Hư hại nhẹ, không ảnh hưởng vận hành.", "Nhẹ", 10 },
                    { 2, "Hư hại vừa, cần sửa chữa.", "Vừa", 30 },
                    { 3, "Hư hại nặng, sửa chữa tốn kém.", "Nặng", 50 },
                    { 4, "Máy không còn sử dụng được.", "Hỏng hoàn toàn", 100 }
                });

            // Đơn đã checkout trước migration dùng bảng seed làm snapshot (khóa JSON khớp DamageLevelSnapshot).
            migrationBuilder.Sql("""
                UPDATE rental.rental_items
                SET "DamageLevelsSnapshot" = (
                    SELECT jsonb_agg(jsonb_build_object('Level', "Level", 'Name', "Name", 'Percent', "Percent") ORDER BY "Level")
                    FROM rental.damage_levels)
                WHERE "DamageLevelsSnapshot" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "damage_levels",
                schema: "rental");

            migrationBuilder.DropColumn(
                name: "DamageLevelsSnapshot",
                schema: "rental",
                table: "rental_items");
        }
    }
}
