using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentTypeSpecSchemaAndDropOrganizationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EquipmentTypeSpecSchemas",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentTypeSpecSchemas", x => x.Id);
                    table.UniqueConstraint("AK_EquipmentTypeSpecSchemas_EquipmentTypeId_Version", x => new { x.EquipmentTypeId, x.Version });
                    table.CheckConstraint("CK_EquipmentTypeSpecSchemas_Version_Positive", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_EquipmentTypeSpecSchemas_EquipmentTypes_EquipmentTypeId",
                        column: x => x.EquipmentTypeId,
                        principalSchema: "catalog",
                        principalTable: "EquipmentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_EquipmentTypeSpecSchemas_EquipmentTypeId_Draft",
                schema: "catalog",
                table: "EquipmentTypeSpecSchemas",
                column: "EquipmentTypeId",
                unique: true,
                filter: "\"PublishedAt\" IS NULL");

            migrationBuilder.AddColumn<int>(
                name: "CurrentSchemaVersion",
                schema: "catalog",
                table: "EquipmentTypes",
                type: "integer",
                nullable: true);

            // Backfill published v1 cho toàn bộ EquipmentType hiện có
            migrationBuilder.Sql(
                """
                INSERT INTO catalog."EquipmentTypeSpecSchemas" ("Id", "EquipmentTypeId", "Version", "Definition", "CreatedAt", "PublishedAt")
                SELECT gen_random_uuid(), "Id", 1, '{"fields":[]}'::jsonb, NOW(), NOW()
                FROM catalog."EquipmentTypes";

                UPDATE catalog."EquipmentTypes"
                SET "CurrentSchemaVersion" = 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_EquipmentTypes_OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentTypes_Id_CurrentSchemaVersion",
                schema: "catalog",
                table: "EquipmentTypes",
                columns: new[] { "Id", "CurrentSchemaVersion" });

            migrationBuilder.AddForeignKey(
                name: "FK_EquipmentTypes_EquipmentTypeSpecSchemas_CurrentSchema",
                schema: "catalog",
                table: "EquipmentTypes",
                columns: new[] { "Id", "CurrentSchemaVersion" },
                principalSchema: "catalog",
                principalTable: "EquipmentTypeSpecSchemas",
                principalColumns: new[] { "EquipmentTypeId", "Version" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Rollback không được hỗ trợ vì việc drop OrganizationId và lịch sử schema không thể hoàn nguyên tự động. " +
                "Vui lòng khôi phục cơ sở dữ liệu từ bản sao lưu trước migration.");
        }
    }
}
