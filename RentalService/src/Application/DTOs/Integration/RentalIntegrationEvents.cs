using System.Text.Json.Serialization;

namespace Dprental.RentalService.Application.DTOs.Integration;

public sealed record RentalEventEnvelope<TPayload>(
    [property: JsonPropertyName("messageId")] Guid MessageId,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("producer")] string Producer,
    [property: JsonPropertyName("aggregateId")] string AggregateId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("causationId")] Guid? CausationId,
    [property: JsonPropertyName("payload")] TPayload Payload);

public static class RentalEventTypes
{
    public const string RentalOrderPaid = "RentalOrderPaid";
    public const string RentalSupplierConfirmationRequested = "RentalSupplierConfirmationRequested";
    public const string RentalItemConfirmed = "RentalItemConfirmed";
    public const string RentalItemRejected = "RentalItemRejected";
    public const string RentalItemExpired = "RentalItemExpired";
    public const string RentalOrderExpired = "RentalOrderExpired";
    public const string LatePaymentRefundRequested = "LatePaymentRefundRequested";
    public const string DeadLetter = "DeadLetter";
}

public sealed record RentalSupplierConfirmationRequestedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    Guid EquipmentListingId,
    int Quantity,
    DateTimeOffset SupplierConfirmationDeadline);

public sealed record RentalItemConfirmedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string InventoryConfirmationRef);

public sealed record RentalItemRejectedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string Reason,
    string? OptionalNote);

public sealed record RentalItemExpiredPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset ExpiredAt,
    string Reason);

public sealed record RentalOrderPaidPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal PaidAmount,
    DateTimeOffset PaidAt);

public sealed record RentalOrderExpiredPayload(
    Guid RentalOrderId,
    Guid RenterId,
    DateTimeOffset ExpiredAt,
    string Reason);

public sealed record LatePaymentRefundRequestedPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal TotalRefundAmount,
    DateTimeOffset RequestedAt);

public sealed record DeadLetterPayload(
    string OriginalMessageType,
    string OriginalPayload,
    string Reason,
    DateTimeOffset FailedAt);
