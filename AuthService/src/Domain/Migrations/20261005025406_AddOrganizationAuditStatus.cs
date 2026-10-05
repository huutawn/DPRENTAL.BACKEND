using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationAuditStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "AuditLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "AuditLogs"
                SET "Status" = 0
                WHERE "Result" = 'Success' AND "Action" IN ('Organization_Register', 'Organization_Resubmit', 'Organization_ReviewReminder');

                UPDATE "AuditLogs"
                SET "Status" = 1
                WHERE "Result" = 'Success' AND "Action" IN ('Organization_Approve', 'Organization_Activate');

                UPDATE "AuditLogs"
                SET "Status" = 2
                WHERE "Result" = 'Success' AND "Action" = 'Organization_Reject';

                UPDATE "AuditLogs"
                SET "Status" = 3
                WHERE "Result" = 'Success' AND "Action" = 'Organization_Suspend';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "AuditLogs");
        }
    }
}
