using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeNotificationUrls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Update deep_link_url: replace ^/renter/orders/ with /orders/
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET deep_link_url = REGEXP_REPLACE(deep_link_url, '^/renter/orders/', '/orders/')
WHERE deep_link_url ~ '^/renter/orders/';
");

            // 2. Update deep_link_url: replace ^/supplier/.* with /own/orders
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET deep_link_url = '/own/orders'
WHERE deep_link_url ~ '^/supplier/';
");

            // 3. Update action_url: replace ^/checkout/payment\?orderId= with /orders/
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET action_url = REGEXP_REPLACE(action_url, '^\/checkout\/payment\?orderId=', '/orders/')
WHERE action_url ~ '^\/checkout\/payment\?orderId=';
");

            // 4. Update action_url: replace ^/renter/orders/ with /orders/
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET action_url = REGEXP_REPLACE(action_url, '^/renter/orders/', '/orders/')
WHERE action_url ~ '^/renter/orders/';
");

            // 5. Update action_url: replace ^/supplier/.* with /own/orders
            migrationBuilder.Sql(@"
UPDATE notification.notifications
SET action_url = '/own/orders'
WHERE action_url ~ '^/supplier/';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
