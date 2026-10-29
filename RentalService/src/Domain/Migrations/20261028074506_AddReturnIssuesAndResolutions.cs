using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace RentalService.Domain.Migrations
{
    public partial class AddReturnIssuesAndResolutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_return_request_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_units");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmationDeadlineAt",
                schema: "rental",
                table: "return_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql(
                "UPDATE rental.return_requests SET \"ConfirmationDeadlineAt\" = \"SubmittedAt\" + INTERVAL '24 hours';");
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OverdueNotifiedAt",
                schema: "rental",
                table: "return_request_units",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RejectedAt",
                schema: "rental",
                table: "return_request_units",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "return_request_issues",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterRole = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MediaIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_issues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_return_request_issues_return_requests_ReturnRequestId",
                        column: x => x.ReturnRequestId,
                        principalSchema: "rental",
                        principalTable: "return_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_request_resolutions",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    DecisionNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ResolutionMediaIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_resolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_return_request_resolutions_return_requests_ReturnRequestId",
                        column: x => x.ReturnRequestId,
                        principalSchema: "rental",
                        principalTable: "return_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_request_issue_units",
                schema: "rental",
                columns: table => new
                {
                    ReturnRequestIssueId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalUnitAssignmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_issue_units", x => new { x.ReturnRequestIssueId, x.RentalUnitAssignmentId });
                    table.ForeignKey(
                        name: "FK_return_request_issue_units_rental_unit_assignments_RentalUn~",
                        column: x => x.RentalUnitAssignmentId,
                        principalSchema: "rental",
                        principalTable: "rental_unit_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_request_issue_units_return_request_issues_ReturnRequ~",
                        column: x => x.ReturnRequestIssueId,
                        principalSchema: "rental",
                        principalTable: "return_request_issues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "return_request_resolution_units",
                schema: "rental",
                columns: table => new
                {
                    ReturnRequestResolutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalUnitAssignmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_resolution_units", x => new { x.ReturnRequestResolutionId, x.RentalUnitAssignmentId });
                    table.ForeignKey(
                        name: "FK_return_request_resolution_units_rental_unit_assignments_Ren~",
                        column: x => x.RentalUnitAssignmentId,
                        principalSchema: "rental",
                        principalTable: "rental_unit_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_request_resolution_units_return_request_resolutions_~",
                        column: x => x.ReturnRequestResolutionId,
                        principalSchema: "rental",
                        principalTable: "return_request_resolutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_ConfirmationDeadlineAt",
                schema: "rental",
                table: "return_requests",
                column: "ConfirmationDeadlineAt");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_units_ClosedAt_RejectedAt_OverdueNotifiedAt",
                schema: "rental",
                table: "return_request_units",
                columns: new[] { "ClosedAt", "RejectedAt", "OverdueNotifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_return_request_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_units",
                column: "RentalUnitAssignmentId",
                unique: true,
                filter: "\"ClosedAt\" IS NULL AND \"RejectedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_issue_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_issue_units",
                column: "RentalUnitAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_issues_ReturnRequestId",
                schema: "rental",
                table: "return_request_issues",
                column: "ReturnRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_resolution_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_resolution_units",
                column: "RentalUnitAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_resolutions_ReturnRequestId",
                schema: "rental",
                table: "return_request_resolutions",
                column: "ReturnRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "return_request_issue_units",
                schema: "rental");

            migrationBuilder.DropTable(
                name: "return_request_resolution_units",
                schema: "rental");

            migrationBuilder.DropTable(
                name: "return_request_issues",
                schema: "rental");

            migrationBuilder.DropTable(
                name: "return_request_resolutions",
                schema: "rental");

            migrationBuilder.DropIndex(
                name: "IX_return_requests_ConfirmationDeadlineAt",
                schema: "rental",
                table: "return_requests");

            migrationBuilder.DropIndex(
                name: "IX_return_request_units_ClosedAt_RejectedAt_OverdueNotifiedAt",
                schema: "rental",
                table: "return_request_units");

            migrationBuilder.DropIndex(
                name: "IX_return_request_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_units");

            migrationBuilder.DropColumn(
                name: "ConfirmationDeadlineAt",
                schema: "rental",
                table: "return_requests");

            migrationBuilder.DropColumn(
                name: "OverdueNotifiedAt",
                schema: "rental",
                table: "return_request_units");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                schema: "rental",
                table: "return_request_units");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_units",
                column: "RentalUnitAssignmentId",
                unique: true,
                filter: "\"ClosedAt\" IS NULL");
        }
    }
}
