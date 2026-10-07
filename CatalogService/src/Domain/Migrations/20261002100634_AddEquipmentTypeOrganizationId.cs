using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatalogService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentTypeOrganizationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes",
                type: "uuid",
                nullable: true);
            migrationBuilder.CreateIndex(
                name: "IX_EquipmentTypes_OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EquipmentTypes_OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes");
            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "catalog",
                table: "EquipmentTypes");
        }
    }
}
