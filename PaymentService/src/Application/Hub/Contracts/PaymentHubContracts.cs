namespace Dprental.PaymentService.Application.Hub.Contracts;

public static class PaymentHubEventNames
{
    public const string PaymentCompleted = "PaymentCompleted";
}

public sealed record PaymentHubEnvelope<T>(
    Guid EventId,
    int Version,
    DateTimeOffset OccurredAt,
    T Data);

public sealed record PaymentCompletedPayload(
    string InvoiceRef,
    Guid RentalOrderId,
    string Status,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PaidAt,
    long Revision);
