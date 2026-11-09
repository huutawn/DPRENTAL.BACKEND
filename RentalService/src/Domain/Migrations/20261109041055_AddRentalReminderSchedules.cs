using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861
#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalReminderSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rental_reminder_schedules",
                schema: "rental",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    next_trigger_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_notified_date = table.Column<DateOnly>(type: "date", nullable: true),
                    last_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    overdue_day_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    remaining_unit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    leased_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    leased_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rental_reminder_schedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rental_reminder_schedules_claim",
                schema: "rental",
                table: "rental_reminder_schedules",
                columns: new[] { "status", "next_trigger_at", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_rental_reminder_schedules_order",
                schema: "rental",
                table: "rental_reminder_schedules",
                column: "rental_order_id");

            migrationBuilder.CreateIndex(
                name: "uq_rental_reminder_schedules_item_type",
                schema: "rental",
                table: "rental_reminder_schedules",
                columns: new[] { "rental_item_id", "schedule_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rental_reminder_schedules",
                schema: "rental");
        }
    }
}
