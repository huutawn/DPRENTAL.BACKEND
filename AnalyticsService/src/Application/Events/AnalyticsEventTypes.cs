namespace Dprental.AnalyticsService.Application.Events;

public static class AnalyticsEventTopics
{
    public const string RentalEventsV1 = "rental.events.v1";
    public const string PaymentAnalyticsV1 = "payment.analytics.v1";
}

/// <summary>Event Payment phát riêng cho Analytics trên payment.analytics.v1 (có proof ký số).</summary>
public static class PaymentAnalyticsEventTypes
{
    public const string InvoicePaidFinancial = "InvoicePaidFinancial";
    public const string RentalItemFinancialSettled = "RentalItemFinancialSettled";
    public const string RefundRequestedFinancial = "RefundRequestedFinancial";
    public const string RefundCompletedFinancial = "RefundCompletedFinancial";
    public const string SupplierPayoutCompleted = "SupplierPayoutCompleted";

    public static bool IsFinancial(string? messageType) => messageType is
        InvoicePaidFinancial or RentalItemFinancialSettled or RefundRequestedFinancial or RefundCompletedFinancial or SupplierPayoutCompleted;
}

/// <summary>Event vòng đời đơn thuê trên rental.events.v1 mà Analytics chiếu vào read model.</summary>
public static class RentalAnalyticsEventTypes
{
    public const string RentalOrderCreated = "RentalOrderCreated";
    public const string RentalOrderPaid = "RentalOrderPaid";
    public const string RentalOrderCompleted = "RentalOrderCompleted";
    public const string RentalOrderCancelled = "RentalOrderCancelled";
    public const string RentalOrderExpired = "RentalOrderExpired";
    public const string RentalOrderRejected = "RentalOrderRejected";
    public const string RentalItemConfirmed = "RentalItemConfirmed";
    public const string RentalItemRejected = "RentalItemRejected";
    public const string RentalItemExpired = "RentalItemExpired";
    public const string RentalItemCancelled = "RentalItemCancelled";
    public const string RentalItemHandoverCompleted = "RentalItemHandoverCompleted";
    public const string RentalItemReturned = "RentalItemReturned";
    public const string RentalItemCompleted = "RentalItemCompleted";
}
