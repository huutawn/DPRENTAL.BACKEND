namespace Dprental.RentalService.Application.Hubs;

/// <summary>
/// Hợp đồng thông báo SignalR invalidation thời gian thực cho người thuê và nhà cung cấp.
/// Chỉ có đúng 2 phương thức typed thông báo, gửi sau khi database transaction ngoài cùng đã commit thành công.
/// </summary>
public interface IOrderHubNotifier
{
    Task NotifyRenterOrderChangedAsync(
        Guid renterId,
        RenterOrderChangedNotification notification,
        CancellationToken cancellationToken = default);

    Task NotifySupplierSubOrderChangedAsync(
        Guid supplierId,
        SupplierSubOrderChangedNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload thông báo thay đổi đơn hàng gửi tới group người thuê renter:{renterId:D}.
/// </summary>
public sealed record RenterOrderChangedNotification(
    Guid EventId,
    Guid OrderId,
    long Version,
    string ChangeType,
    DateTimeOffset OccurredAt);

/// <summary>
/// Payload thông báo thay đổi đơn con gửi tới group nhà cung cấp supplier:{supplierId:D}.
/// </summary>
public sealed record SupplierSubOrderChangedNotification(
    Guid EventId,
    Guid OrderId,
    Guid SubOrderId,
    long OrderVersion,
    string ChangeType,
    DateTimeOffset OccurredAt);

/// <summary>
/// Danh sách hữu hạn các loại thay đổi nghiệp vụ công khai (ChangeType) cho SignalR invalidation.
/// </summary>
public static class OrderChangeTypes
{
    public const string OrderCreated = "ORDER_CREATED";
    public const string InvoiceAttached = "INVOICE_ATTACHED";
    public const string PaymentConfirmed = "PAYMENT_CONFIRMED";
    public const string PaymentRecoveryPending = "PAYMENT_RECOVERY_PENDING";
    public const string PaymentRefundRequired = "PAYMENT_REFUND_REQUIRED";
    public const string DeliveryNoteUpdated = "DELIVERY_NOTE_UPDATED";
    public const string SupplierConfirmed = "SUPPLIER_CONFIRMED";
    public const string SupplierRejected = "SUPPLIER_REJECTED";
    public const string HandoverConfirmed = "HANDOVER_CONFIRMED";
    public const string HandoverAcknowledged = "HANDOVER_ACKNOWLEDGED";
    public const string AllocationChanged = "ALLOCATION_CHANGED";
    public const string OrderCancelled = "ORDER_CANCELLED";
    public const string ItemCancelled = "ITEM_CANCELLED";
    public const string PaymentExpired = "PAYMENT_EXPIRED";
    public const string SupplierConfirmationExpired = "SUPPLIER_CONFIRMATION_EXPIRED";
    public const string HandoverOverdue = "HANDOVER_OVERDUE";
    public const string HandoverExpired = "HANDOVER_EXPIRED";
    public const string ReturnRecorded = "RETURN_RECORDED";
    public const string SettlementChanged = "SETTLEMENT_CHANGED";
    public const string RefundChanged = "REFUND_CHANGED";
}
