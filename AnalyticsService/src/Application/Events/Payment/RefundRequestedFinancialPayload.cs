using System.Text.Json.Serialization;

namespace Dprental.AnalyticsService.Application.Events.Payment;

public sealed record RefundRequestedFinancialPayload(
    [property: JsonPropertyName("refundRef")] string RefundRef,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid? RentalItemId,
    [property: JsonPropertyName("refundKind")] string RefundKind,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt);
