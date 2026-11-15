using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalReminderUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM notification.delivery_jobs dj
                WHERE dj.notification_id IN (
                    SELECT n.id
                    FROM notification.notifications n
                    JOIN (
                        SELECT id,
                               ROW_NUMBER() OVER (
                                   PARTITION BY recipient_user_id, rental_item_id, notification_type, resource_id
                                   ORDER BY created_at ASC, id ASC
                               ) AS rn
                        FROM notification.notifications
                        WHERE source_event_type = 'RentalReminderDue'
                          AND rental_item_id IS NOT NULL
                    ) dup ON n.id = dup.id
                    WHERE dup.rn > 1
                );

                DELETE FROM notification.notifications n
                USING (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY recipient_user_id, rental_item_id, notification_type, resource_id
                               ORDER BY created_at ASC, id ASC
                           ) AS rn
                    FROM notification.notifications
                    WHERE source_event_type = 'RentalReminderDue'
                      AND rental_item_id IS NOT NULL
                ) dup
                WHERE n.id = dup.id AND dup.rn > 1;
            ");
            migrationBuilder.CreateIndex(
                name: "uq_notifications_reminder_dedup",
                schema: "notification",
                table: "notifications",
                columns: new[] { "recipient_user_id", "rental_item_id", "notification_type", "resource_id" },
                unique: true,
                filter: "source_event_type = 'RentalReminderDue' AND rental_item_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_notifications_reminder_dedup",
                schema: "notification",
                table: "notifications");
        }
    }
}
