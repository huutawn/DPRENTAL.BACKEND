namespace Dprental.AnalyticsService.Application.Kafka.Contracts;

/// <summary>
/// Tên các event Analytics xử lý (giá trị messageType trong envelope).
/// Danh sách thật sự được xử lý là bảng định tuyến trong AnalyticsEventDispatcher; event khác được bỏ qua.
/// </summary>
public static class AnalyticsMessageTypes
{
    // rental.events.v1
    public const string RentalOrderCreated = "RentalOrderCreated";
    public const string RentalOrderPaid = "RentalOrderPaid";
    public const string RentalOrderCompleted = "RentalOrderCompleted";
    public const string RentalOrderCancelled = "RentalOrderCancelled";
    public const string RentalOrderExpired = "RentalOrderExpired";
    public const string RentalItemConfirmed = "RentalItemConfirmed";
    public const string RentalItemRejected = "RentalItemRejected";
    public const string RentalItemExpired = "RentalItemExpired";
    public const string RentalItemCancelled = "RentalItemCancelled";
    public const string RentalItemHandoverCompleted = "RentalItemHandoverCompleted";
    public const string RentalItemReturned = "RentalItemReturned";
    public const string RentalItemCompleted = "RentalItemCompleted";

    // payment.events.v1
    public const string RentalSettled = "RentalSettled";
    public const string RefundCompleted = "RefundCompleted";
}
