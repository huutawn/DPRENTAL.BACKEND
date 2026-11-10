using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseTokenToRentalReminderSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lease_token",
                schema: "rental",
                table: "rental_reminder_schedules",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lease_token",
                schema: "rental",
                table: "rental_reminder_schedules");
        }
    }
}
