using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ProfileCompletedAtUtc",
                schema: "auth",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE auth."Users"
                SET "ProfileCompletedAtUtc" = "CreatedAt"
                WHERE "ProfileCompletedAtUtc" IS NULL
                  AND "CitizenId" IS NOT NULL AND "PhoneNumber" IS NOT NULL AND "DateOfBirth" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProfileCompletedAtUtc",
                schema: "auth",
                table: "Users");
        }
    }
}
