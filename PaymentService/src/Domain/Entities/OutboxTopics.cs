namespace Dprental.PaymentService.Domain.Entities;

public static class OutboxTopics
{
    public const string PaymentEventsV1 = "payment.events.v1";
    public const string PaymentAnalyticsV1 = "payment.analytics.v1";

    public const string RentalAudience = "rental-service";
    public const string AnalyticsAudience = "analytics-service";

    public const string InvoicePaidFinancialMessageType = "InvoicePaidFinancial";
    public const string RentalItemFinancialSettledMessageType = "RentalItemFinancialSettled";
    public const string RefundCompletedFinancialMessageType = "RefundCompletedFinancial";
    public const string SupplierPayoutCompletedMessageType = "SupplierPayoutCompleted";

    public static string? ResolveAudience(string? topic) => topic switch
    {
        PaymentEventsV1 => RentalAudience,
        PaymentAnalyticsV1 => AnalyticsAudience,
        _ => null
    };

    public static string ResolveTopicForMessageType(string messageType) => messageType switch
    {
        InvoicePaidFinancialMessageType or RentalItemFinancialSettledMessageType or
            RefundCompletedFinancialMessageType or SupplierPayoutCompletedMessageType => PaymentAnalyticsV1,
        _ => PaymentEventsV1
    };

    public static bool IsValidRoute(string? messageType, string? topic) =>
        !string.IsNullOrWhiteSpace(messageType) &&
        string.Equals(ResolveTopicForMessageType(messageType.Trim()), topic, StringComparison.Ordinal);
}
