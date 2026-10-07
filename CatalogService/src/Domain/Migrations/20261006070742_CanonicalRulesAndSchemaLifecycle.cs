using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalRulesAndSchemaLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dữ liệu cũ: đổi Retired thành Deprecated TRƯỚC khi thêm check constraint
            migrationBuilder.Sql("""
                UPDATE catalog."SchemaVersions" SET "Status" = 'Deprecated' WHERE "Status" = 'Retired';
                """);

            // Cột bóng SchemaVersionId1 chỉ có ở một số database (do khóa ngoại thừa trong snapshot cũ), nên xóa nếu tồn tại
            migrationBuilder.Sql("""
                ALTER TABLE catalog."SpecFields" DROP CONSTRAINT IF EXISTS "FK_SpecFields_SchemaVersions_SchemaVersionId1";
                DROP INDEX IF EXISTS catalog."IX_SpecFields_SchemaVersionId1";
                ALTER TABLE catalog."SpecFields" DROP COLUMN IF EXISTS "SchemaVersionId1";
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeprecatedAt",
                schema: "catalog",
                table: "SchemaVersions",
                type: "timestamp with time zone",
                nullable: true);

            // Dữ liệu cũ: version đã Deprecated nhưng chưa có thời điểm thì lấy thời điểm migration chạy
            migrationBuilder.Sql("""
                UPDATE catalog."SchemaVersions"
                SET "DeprecatedAt" = now()
                WHERE "Status" = 'Deprecated' AND "DeprecatedAt" IS NULL;
                """);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                schema: "catalog",
                table: "EquipmentTypes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Dữ liệu cũ: điền NormalizedName khớp CatalogNormalizer (gộp mọi khoảng trắng thành một dấu cách, cắt hai đầu, chữ thường)
            migrationBuilder.Sql("""
                UPDATE catalog."EquipmentTypes"
                SET "NormalizedName" = lower(btrim(regexp_replace("Name", '\s+', ' ', 'g')))
                WHERE "NormalizedName" = '';
                """);

            // Dữ liệu cũ: mỗi EquipmentType phải có một SpecSchema để Admin gọi POST drafts tạo v1
            migrationBuilder.Sql("""
                INSERT INTO catalog."SpecSchemas" ("Id", "EquipmentTypeId")
                SELECT gen_random_uuid(), e."Id"
                FROM catalog."EquipmentTypes" e
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog."SpecSchemas" s WHERE s."EquipmentTypeId" = e."Id");
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SchemaVersions_Status",
                schema: "catalog",
                table: "SchemaVersions",
                sql: "\"Status\" IN ('Draft', 'Published', 'Deprecated')");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentTypes_NormalizedName",
                schema: "catalog",
                table: "EquipmentTypes",
                column: "NormalizedName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SchemaVersions_Status",
                schema: "catalog",
                table: "SchemaVersions");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentTypes_NormalizedName",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "DeprecatedAt",
                schema: "catalog",
                table: "SchemaVersions");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.AddColumn<Guid>(
                name: "SchemaVersionId1",
                schema: "catalog",
                table: "SpecFields",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpecFields_SchemaVersionId1",
                schema: "catalog",
                table: "SpecFields",
                column: "SchemaVersionId1");

            migrationBuilder.AddForeignKey(
                name: "FK_SpecFields_SchemaVersions_SchemaVersionId1",
                schema: "catalog",
                table: "SpecFields",
                column: "SchemaVersionId1",
                principalSchema: "catalog",
                principalTable: "SchemaVersions",
                principalColumn: "Id");
        }
    }
}