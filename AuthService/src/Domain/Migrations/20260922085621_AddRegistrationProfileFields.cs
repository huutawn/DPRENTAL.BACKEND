using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrationProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                schema: "auth",
                table: "Users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CitizenId",
                schema: "auth",
                table: "Users",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "auth",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                schema: "auth",
                table: "Users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "auth",
                table: "Organizations",
                type: "character varying(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessLicenseDocumentUrl",
                schema: "auth",
                table: "Organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BusinessLicenseIssuedDate",
                schema: "auth",
                table: "Organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessLicenseIssuedPlace",
                schema: "auth",
                table: "Organizations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessLicenseNumber",
                schema: "auth",
                table: "Organizations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "auth",
                table: "Organizations",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPhone",
                schema: "auth",
                table: "Organizations",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "auth",
                table: "Organizations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EstablishedDate",
                schema: "auth",
                table: "Organizations",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalRepresentativeCitizenId",
                schema: "auth",
                table: "Organizations",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalRepresentativeName",
                schema: "auth",
                table: "Organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                schema: "auth",
                table: "Organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Province",
                schema: "auth",
                table: "Organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                schema: "auth",
                table: "Organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAtUtc",
                schema: "auth",
                table: "Organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByUserId",
                schema: "auth",
                table: "Organizations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "auth",
                table: "Organizations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Chuyển dữ liệu IsActive cũ sang Status (enum OrganizationStatus: 1 = Active, 3 = Suspended)
            // trước khi xoá cột, để organization đang hoạt động không bị rơi về Pending.
            migrationBuilder.Sql("""
                UPDATE auth."Organizations"
                SET "Status" = CASE WHEN "IsActive" THEN 1 ELSE 3 END;
                """);

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.AddColumn<string>(
                name: "TaxCode",
                schema: "auth",
                table: "Organizations",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ward",
                schema: "auth",
                table: "Organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                schema: "auth",
                table: "Organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_CitizenId",
                schema: "auth",
                table: "Users",
                column: "CitizenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Status",
                schema: "auth",
                table: "Organizations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations",
                column: "TaxCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_CitizenId",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_Status",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_TaxCode",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CitizenId",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Address",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "BusinessLicenseDocumentUrl",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "BusinessLicenseIssuedDate",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "BusinessLicenseIssuedPlace",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "BusinessLicenseNumber",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ContactPhone",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "EstablishedDate",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "LegalRepresentativeCitizenId",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "LegalRepresentativeName",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Province",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ReviewedAtUtc",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "ReviewedByUserId",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "auth",
                table: "Organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE auth."Organizations"
                SET "IsActive" = ("Status" = 1);
                """);

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "TaxCode",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Ward",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Website",
                schema: "auth",
                table: "Organizations");
        }
    }
}
