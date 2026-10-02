using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationSuspensionAndAuditReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SuspensionReason",
                schema: "auth",
                table: "Organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "AuditLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Target_Timestamp",
                table: "AuditLogs",
                columns: new[] { "Target", "Timestamp" });

            migrationBuilder.Sql(
                """
                UPDATE "AuditLogs"
                SET "Reason" = SUBSTRING("Result" FROM 10),
                    "Result" = 'Success'
                WHERE "Action" = 'Organization_Reject' AND "Result" LIKE 'Success: %';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "AuditLogs"
                SET "Result" = 'Success: ' || "Reason"
                WHERE "Action" = 'Organization_Reject' AND "Reason" IS NOT NULL AND "Result" = 'Success';
                """);

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Target_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SuspensionReason",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "AuditLogs");
        }
    }
}
