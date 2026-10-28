using System.Text.Json.Serialization;

namespace Dprental.PaymentService.Application.DTOs.Events;

public sealed record RefundRequestedFinancialPayload(
    [property: JsonPropertyName("refundRef")] string RefundRef,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid? RentalItemId,
    [property: JsonPropertyName("refundKind")] string RefundKind,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);
