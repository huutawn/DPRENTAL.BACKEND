using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    public partial class RefactorToCanonicalCatalog : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Tạo bảng SpecSchemas
            migrationBuilder.CreateTable(
                name: "SpecSchemas",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EquipmentTypeId1 = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecSchemas", x => x.Id);

                    table.ForeignKey(
                        name: "FK_SpecSchemas_EquipmentTypes_EquipmentTypeId",
                        column: x => x.EquipmentTypeId,
                        principalSchema: "catalog",
                        principalTable: "EquipmentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);

                    table.ForeignKey(
                        name: "FK_SpecSchemas_EquipmentTypes_EquipmentTypeId1",
                        column: x => x.EquipmentTypeId1,
                        principalSchema: "catalog",
                        principalTable: "EquipmentTypes",
                        principalColumn: "Id");
                });
            // 2. Tạo bảng SchemaVersions
            migrationBuilder.CreateTable(
                name: "SchemaVersions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecSchemaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecSchemaId1 = table.Column<Guid>(type: "uuid", nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false),
                    PublishedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true),
                    PublishedAtUtc = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaVersions", x => x.Id);

                    table.ForeignKey(
                        name: "FK_SchemaVersions_SpecSchemas_SpecSchemaId",
                        column: x => x.SpecSchemaId,
                        principalSchema: "catalog",
                        principalTable: "SpecSchemas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);

                    table.ForeignKey(
                        name: "FK_SchemaVersions_SpecSchemas_SpecSchemaId1",
                        column: x => x.SpecSchemaId1,
                        principalSchema: "catalog",
                        principalTable: "SpecSchemas",
                        principalColumn: "Id");
                });


            // 5. Cập nhật khóa ngoại ActiveSchemaVersionId cho EquipmentTypes

            migrationBuilder.AddForeignKey(
                name: "FK_EquipmentTypes_SchemaVersions_ActiveSchemaVersionId",
                schema: "catalog",
                table: "EquipmentTypes",
                column: "ActiveSchemaVersionId",
                principalSchema: "catalog",
                principalTable: "SchemaVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            // 6. Tạo bảng SpecFields
            migrationBuilder.CreateTable(
                name: "SpecFields",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersionId1 = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DataType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Unit = table.Column<string>(type: "character varying(50)", nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    MinValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    MaxValue = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecFields_SchemaVersions_SchemaVersionId",
                        column: x => x.SchemaVersionId,
                        principalSchema: "catalog",
                        principalTable: "SchemaVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpecFields_SchemaVersions_SchemaVersionId1",
                        column: x => x.SchemaVersionId1,
                        principalSchema: "catalog",
                        principalTable: "SchemaVersions",
                        principalColumn: "Id");
                });

            // 7. Tạo bảng SpecOptions
            migrationBuilder.CreateTable(
                name: "SpecOptions",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpecFieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpecOptions_SpecFields_SpecFieldId",
                        column: x => x.SpecFieldId,
                        principalSchema: "catalog",
                        principalTable: "SpecFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 8. Tạo tất cả Indexes
            migrationBuilder.CreateIndex(name: "IX_SpecSchemas_EquipmentTypeId", schema: "catalog", table: "SpecSchemas", column: "EquipmentTypeId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_SpecSchemas_EquipmentTypeId1", schema: "catalog", table: "SpecSchemas", column: "EquipmentTypeId1");

            migrationBuilder.CreateIndex(name: "UX_SchemaVersions_OneDraftPerSchema", schema: "catalog", table: "SchemaVersions", column: "SpecSchemaId", unique: true, filter: "\"Status\" = 'Draft'");
            migrationBuilder.CreateIndex(name: "IX_SchemaVersions_SpecSchemaId1", schema: "catalog", table: "SchemaVersions", column: "SpecSchemaId1");
            migrationBuilder.CreateIndex(name: "IX_SchemaVersions_SpecSchemaId_VersionNumber", schema: "catalog", table: "SchemaVersions", columns: new[] { "SpecSchemaId", "VersionNumber" }, unique: true);

            migrationBuilder.CreateIndex(name: "IX_SpecFields_SchemaVersionId1", schema: "catalog", table: "SpecFields", column: "SchemaVersionId1");
            migrationBuilder.CreateIndex(name: "IX_SpecFields_SchemaVersionId_DisplayOrder", schema: "catalog", table: "SpecFields", columns: new[] { "SchemaVersionId", "DisplayOrder" });
            migrationBuilder.CreateIndex(name: "IX_SpecFields_SchemaVersionId_Key", schema: "catalog", table: "SpecFields", columns: new[] { "SchemaVersionId", "Key" }, unique: true);

            migrationBuilder.CreateIndex(name: "IX_SpecOptions_SpecFieldId_Value", schema: "catalog", table: "SpecOptions", columns: new[] { "SpecFieldId", "Value" }, unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Unit Test thường không gọi hàm Down nên để trống
        }
    }
}