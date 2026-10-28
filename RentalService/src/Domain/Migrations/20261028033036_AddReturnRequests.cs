using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddReturnRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "return_requests",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActualReturnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SubmittedById = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_return_requests_rental_items_RentalItemId",
                        column: x => x.RentalItemId,
                        principalSchema: "rental",
                        principalTable: "rental_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "return_request_units",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalUnitAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MediaIds = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_return_request_units", x => x.Id);
                    table.ForeignKey(
                        name: "FK_return_request_units_rental_unit_assignments_RentalUnitAssi~",
                        column: x => x.RentalUnitAssignmentId,
                        principalSchema: "rental",
                        principalTable: "rental_unit_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_return_request_units_return_requests_ReturnRequestId",
                        column: x => x.ReturnRequestId,
                        principalSchema: "rental",
                        principalTable: "return_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_return_request_units_RentalUnitAssignmentId",
                schema: "rental",
                table: "return_request_units",
                column: "RentalUnitAssignmentId",
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_return_request_units_ReturnRequestId",
                schema: "rental",
                table: "return_request_units",
                column: "ReturnRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_return_requests_RentalItemId",
                schema: "rental",
                table: "return_requests",
                column: "RentalItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "return_request_units",
                schema: "rental");

            migrationBuilder.DropTable(
                name: "return_requests",
                schema: "rental");
        }
    }
}
