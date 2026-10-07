using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class UseEquipmentTypeSpecSchemasOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE catalog."EquipmentTypes"
                    DROP CONSTRAINT IF EXISTS "FK_EquipmentTypes_SchemaVersions_ActiveSchemaVersionId";
                DROP INDEX IF EXISTS catalog."IX_EquipmentTypes_ActiveSchemaVersionId";
                ALTER TABLE catalog."EquipmentTypes"
                    DROP COLUMN IF EXISTS "ActiveSchemaVersionId";

                DROP TABLE IF EXISTS catalog."SpecOptions";
                DROP TABLE IF EXISTS catalog."SpecFields";
                DROP TABLE IF EXISTS catalog."SchemaVersions";
                DROP TABLE IF EXISTS catalog."SpecSchemas";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Rollback is not supported because the legacy schema tables are removed. Restore from a database backup instead.");
        }
    }
}
