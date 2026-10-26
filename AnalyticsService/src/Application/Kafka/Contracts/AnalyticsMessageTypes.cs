namespace Dprental.AnalyticsService.Application.Kafka.Contracts;

/// <summary>
/// Tên các event Analytics xử lý (giá trị messageType trong envelope). Event khác được bỏ qua.
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

    private static readonly HashSet<string> Handled = new(StringComparer.Ordinal)
    {
        RentalOrderCreated,
        RentalOrderPaid,
        RentalOrderCompleted,
        RentalOrderCancelled,
        RentalOrderExpired,
        RentalItemConfirmed,
        RentalItemRejected,
        RentalItemExpired,
        RentalItemCancelled,
        RentalItemHandoverCompleted,
        RentalItemReturned,
        RentalItemCompleted,
        RentalSettled,
        RefundCompleted
    };

    /// <summary>
    /// True nếu Analytics cần xử lý loại event này; false thì chỉ đánh dấu đã đọc rồi bỏ qua.
    /// </summary>
    public static bool IsHandled(string messageType) => Handled.Contains(messageType);
}
