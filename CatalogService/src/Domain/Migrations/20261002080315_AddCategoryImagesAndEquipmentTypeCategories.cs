using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryImagesAndEquipmentTypeCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Thêm cột ảnh đại diện nullable cho Category
            migrationBuilder.AddColumn<Guid>(
                name: "ImageMediaId",
                schema: "catalog",
                table: "Categories",
                type: "uuid",
                nullable: true);

            // 2. Tạo bảng nối catalog.EquipmentTypeCategories với FK và PK ghép
            migrationBuilder.CreateTable(
                name: "EquipmentTypeCategories",
                schema: "catalog",
                columns: table => new
                {
                    EquipmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentTypeCategories", x => new { x.EquipmentTypeId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_EquipmentTypeCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "catalog",
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EquipmentTypeCategories_EquipmentTypes_EquipmentTypeId",
                        column: x => x.EquipmentTypeId,
                        principalSchema: "catalog",
                        principalTable: "EquipmentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 3. Tạo index theo CategoryId trên bảng nối
            migrationBuilder.CreateIndex(
                name: "IX_EquipmentTypeCategories_CategoryId",
                schema: "catalog",
                table: "EquipmentTypeCategories",
                column: "CategoryId");

            // 4. Sao chép đầy đủ quan hệ từ EquipmentTypes.CategoryId sang bảng nối
            migrationBuilder.Sql("""
                INSERT INTO catalog."EquipmentTypeCategories" ("EquipmentTypeId", "CategoryId")
                SELECT "Id", "CategoryId"
                FROM catalog."EquipmentTypes"
                WHERE "CategoryId" IS NOT NULL;
                """);

            // 5. Kiểm tra số liên kết trước khi bỏ cột/FK/index cũ
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    orig_count integer;
                    migrated_count integer;
                BEGIN
                    SELECT count(*) INTO orig_count FROM catalog."EquipmentTypes" WHERE "CategoryId" IS NOT NULL;
                    SELECT count(*) INTO migrated_count FROM catalog."EquipmentTypeCategories";
                    IF orig_count <> migrated_count THEN
                        RAISE EXCEPTION 'Migration count mismatch: expected %, got %', orig_count, migrated_count;
                    END IF;
                END $$;
                """);

            // 6. Xóa FK, Index và Cột CategoryId cũ trên EquipmentTypes
            migrationBuilder.DropForeignKey(
                name: "FK_EquipmentTypes_Categories_CategoryId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentTypes_CategoryId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                schema: "catalog",
                table: "EquipmentTypes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrade is not supported because many-to-many relationship cannot be reliably converted back to a single category. Restore from backup to rollback.");
        }
    }
}
