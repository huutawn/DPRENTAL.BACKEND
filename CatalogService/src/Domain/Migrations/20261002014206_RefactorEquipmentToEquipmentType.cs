using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RefactorEquipmentToEquipmentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Khóa bảng và kiểm tra duplicate Code trước khi thay đổi schema.
            migrationBuilder.Sql("""
        LOCK TABLE catalog."Equipments" IN ACCESS EXCLUSIVE MODE;
        LOCK TABLE catalog."EquipmentMedia" IN ACCESS EXCLUSIVE MODE;

        DO $$
        DECLARE
            dup_count integer;
            dup_codes text;
        BEGIN
            SELECT count(*), string_agg(quote_literal("Code"), ', ')
            INTO dup_count, dup_codes
            FROM (
                SELECT "Code"
                FROM catalog."Equipments"
                GROUP BY "Code"
                HAVING count(*) > 1
            ) dups;

            IF dup_count > 0 THEN
                RAISE EXCEPTION
                    'Cannot refactor Equipments to EquipmentTypes: duplicate Code values found: %',
                    dup_codes;
            END IF;
        END $$;
        """);

            // 2. Rename tables.
            migrationBuilder.RenameTable(
                name: "Equipments",
                schema: "catalog",
                newName: "EquipmentTypes",
                newSchema: "catalog");

            migrationBuilder.RenameTable(
                name: "EquipmentMedia",
                schema: "catalog",
                newName: "EquipmentTypeMedia",
                newSchema: "catalog");

            // 3. Rename EquipmentMedia.EquipmentId -> EquipmentTypeId.
            migrationBuilder.RenameColumn(
                name: "EquipmentId",
                schema: "catalog",
                table: "EquipmentTypeMedia",
                newName: "EquipmentTypeId");

            // 4. Rename timestamps.
            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                schema: "catalog",
                table: "EquipmentTypes",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "UpdatedAtUtc",
                schema: "catalog",
                table: "EquipmentTypes",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "PublishedAtUtc",
                schema: "catalog",
                table: "EquipmentTypes",
                newName: "PublishedAt");

            // 5. Hidden -> Disabled.
            migrationBuilder.Sql("""
        UPDATE catalog."EquipmentTypes"
        SET "Status" = 'Disabled'
        WHERE "Status" = 'Hidden';
        """);

            // 6. Remove obsolete constraints.
            migrationBuilder.DropCheckConstraint(
                name: "CK_Equipments_DailyPrice_NonNegative",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Equipments_Specifications_IsObject",
                schema: "catalog",
                table: "EquipmentTypes");

            // 7. Remove obsolete indexes.
            migrationBuilder.DropIndex(
                name: "IX_Equipments_DailyPrice",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropIndex(
                name: "IX_Equipments_RegionId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropIndex(
                name: "IX_Equipments_OrganizationId_Code",
                schema: "catalog",
                table: "EquipmentTypes");

            // 8. Remove obsolete columns.
            migrationBuilder.DropColumn(
                name: "Brand",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "DailyPrice",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "RegionId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "Specifications",
                schema: "catalog",
                table: "EquipmentTypes");

            // 9. Rename primary keys.
            migrationBuilder.Sql("""
        ALTER TABLE catalog."EquipmentTypes"
        RENAME CONSTRAINT "PK_Equipments"
        TO "PK_EquipmentTypes";
        """);

            migrationBuilder.Sql("""
        ALTER TABLE catalog."EquipmentTypeMedia"
        RENAME CONSTRAINT "PK_EquipmentMedia"
        TO "PK_EquipmentTypeMedia";
        """);

            // 10. Rename foreign keys.
            migrationBuilder.Sql("""
        ALTER TABLE catalog."EquipmentTypes"
        RENAME CONSTRAINT "FK_Equipments_Categories_CategoryId"
        TO "FK_EquipmentTypes_Categories_CategoryId";
        """);

            migrationBuilder.Sql("""
        ALTER TABLE catalog."EquipmentTypeMedia"
        RENAME CONSTRAINT "FK_EquipmentMedia_Equipments_EquipmentId"
        TO "FK_EquipmentTypeMedia_EquipmentTypes_EquipmentTypeId";
        """);

            // 11. Rename indexes.
            migrationBuilder.Sql("""
        ALTER INDEX catalog."IX_Equipments_CategoryId"
        RENAME TO "IX_EquipmentTypes_CategoryId";
        """);

            migrationBuilder.Sql("""
        ALTER INDEX catalog."IX_Equipments_CreatedAtUtc"
        RENAME TO "IX_EquipmentTypes_CreatedAt";
        """);

            migrationBuilder.Sql("""
        ALTER INDEX catalog."IX_Equipments_PublishedAtUtc"
        RENAME TO "IX_EquipmentTypes_PublishedAt";
        """);

            migrationBuilder.Sql("""
        ALTER INDEX catalog."IX_Equipments_Status"
        RENAME TO "IX_EquipmentTypes_Status";
        """);

            migrationBuilder.Sql("""
        ALTER INDEX catalog."IX_EquipmentMedia_EquipmentId_MediaId"
        RENAME TO "IX_EquipmentTypeMedia_EquipmentTypeId_MediaId";
        """);

            migrationBuilder.Sql("""
        ALTER INDEX catalog."UX_EquipmentMedia_EquipmentId_Thumbnail"
        RENAME TO "UX_EquipmentTypeMedia_EquipmentTypeId_Thumbnail";
        """);

            // 12. Unique Code.
            migrationBuilder.CreateIndex(
                name: "IX_EquipmentTypes_Code",
                schema: "catalog",
                table: "EquipmentTypes",
                column: "Code",
                unique: true);

            // 13. Canonical status constraint.
            migrationBuilder.AddCheckConstraint(
                name: "CK_EquipmentTypes_Status_Canonical",
                schema: "catalog",
                table: "EquipmentTypes",
                sql: "\"Status\" IN ('Draft', 'Published', 'Disabled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Downgrade is not supported because supplier columns and data were permanently removed during refactoring. Restore from backup to rollback.");
        }
    }
}
