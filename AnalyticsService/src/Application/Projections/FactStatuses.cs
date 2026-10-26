namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>Trạng thái đơn lưu trong analytics_rental_orders.Status (dạng chữ, dashboard so theo chuỗi).</summary>
public static class OrderStatuses
{
    public const string Unknown = "UNKNOWN"; // Dòng tạm: event khác đến trước RentalOrderCreated
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Paid = "PAID";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
    public const string Rejected = "REJECTED";

    /// <summary>Trạng thái kết thúc: đơn không còn "đang hoạt động".</summary>
    public static readonly IReadOnlyList<string> Terminal = [Completed, Cancelled, Expired, Rejected];
}

/// <summary>Trạng thái dòng thuê lưu trong analytics_rental_items.Status, cùng tập giá trị với Rental.</summary>
public static class ItemStatuses
{
    public const string Unknown = "UNKNOWN";
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string PendingSupplierConfirmation = "PENDING_SUPPLIER_CONFIRMATION";
    public const string AwaitingHandover = "AWAITING_HANDOVER";
    public const string InRental = "IN_RENTAL";
    public const string PendingSettlement = "PENDING_SETTLEMENT";
    public const string Completed = "COMPLETED";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}
