namespace Dprental.NotificationService.Application.Policies;

public static class NotificationChannelPolicy
{
    /// <summary>
    /// Kiểm tra xem loại thông báo và vai trò người nhận có kích hoạt kênh Email trong MVP Phase 1 hay không.
    /// </summary>
    public static bool ShouldSendEmail(string notificationType, string recipientRole)
    {
        return notificationType switch
        {
            "ORDER_PAID" => true,
            "SUPPLIER_CONFIRMATION_REQUIRED" => true,
            "HANDOVER_OVERDUE_SUPPLIER" => string.Equals(recipientRole, "Supplier", StringComparison.OrdinalIgnoreCase),
            "RETURN_CONFIRMATION_OVERDUE_SUPPLIER" => string.Equals(recipientRole, "Supplier", StringComparison.OrdinalIgnoreCase),
            "ORDER_COMPLETED" => true,
            _ => false
        };
    }
}
