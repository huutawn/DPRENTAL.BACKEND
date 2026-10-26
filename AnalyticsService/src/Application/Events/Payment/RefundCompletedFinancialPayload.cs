using System.Text.Json.Serialization;

namespace Dprental.AnalyticsService.Application.Events.Payment;

public sealed record RefundCompletedFinancialPayload(
    [property: JsonPropertyName("refundId")] Guid RefundId,
    [property: JsonPropertyName("refundRef")] string RefundRef,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid? RentalItemId,
    [property: JsonPropertyName("renterId")] Guid RenterId,
    [property: JsonPropertyName("refundKind")] string RefundKind,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);
