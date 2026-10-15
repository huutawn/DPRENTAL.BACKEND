using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <summary>
    /// Inventory chỉ còn trả tên, giá và ảnh của Listing, nên nơi nhận, loại, hãng, model và thông số trên rental_items
    /// được để trống cho đơn mới. Up chỉ bỏ NOT NULL, đơn cũ giữ nguyên dữ liệu.
    /// Down điền giá trị mặc định cho các dòng trống rồi mới đặt lại NOT NULL; các giá trị đó không phải dữ liệu thật.
    /// </summary>
    public partial class MakeRentalItemListingDetailsOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SpecValuesSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<int>(
                name: "SpecSchemaVersionSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "PickupLocationSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "ModelSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "EquipmentTypeNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<Guid>(
                name: "EquipmentTypeId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "BrandNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<Guid>(
                name: "BrandId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL không cho đặt NOT NULL khi còn dòng trống.
            migrationBuilder.Sql(
                """
                UPDATE rental.rental_items SET
                    "SpecValuesSnapshot" = COALESCE("SpecValuesSnapshot", '{}'::jsonb),
                    "SpecSchemaVersionSnapshot" = COALESCE("SpecSchemaVersionSnapshot", 0),
                    "PickupLocationSnapshot" = COALESCE("PickupLocationSnapshot", ''),
                    "ModelSnapshot" = COALESCE("ModelSnapshot", ''),
                    "EquipmentTypeNameSnapshot" = COALESCE("EquipmentTypeNameSnapshot", ''),
                    "EquipmentTypeId" = COALESCE("EquipmentTypeId", '00000000-0000-0000-0000-000000000000'),
                    "BrandNameSnapshot" = COALESCE("BrandNameSnapshot", ''),
                    "BrandId" = COALESCE("BrandId", '00000000-0000-0000-0000-000000000000');
                """);

            migrationBuilder.AlterColumn<string>(
                name: "SpecValuesSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SpecSchemaVersionSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PickupLocationSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ModelSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EquipmentTypeNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EquipmentTypeId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BrandNameSnapshot",
                schema: "rental",
                table: "rental_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BrandId",
                schema: "rental",
                table: "rental_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
