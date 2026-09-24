using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHasPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasPassword",
                schema: "auth",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE auth."Users" u
                SET "HasPassword" = true
                WHERE u."HasPassword" = false
                  AND NOT EXISTS (
                      SELECT 1 FROM auth."ExternalLogins" e
                      WHERE e."UserId" = u."Id" AND e."CreatedAtUtc" <= u."CreatedAt" + INTERVAL '1 second');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasPassword",
                schema: "auth",
                table: "Users");
        }
    }
}
