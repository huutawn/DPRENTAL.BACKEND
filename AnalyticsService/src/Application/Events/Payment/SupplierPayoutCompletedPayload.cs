using System.Text.Json.Serialization;

namespace Dprental.AnalyticsService.Application.Events.Payment;

public sealed record SupplierPayoutCompletedPayload(
    [property: JsonPropertyName("payoutId")] Guid PayoutId,
    [property: JsonPropertyName("payoutRef")] string PayoutRef,
    [property: JsonPropertyName("settlementId")] Guid SettlementId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId,
    [property: JsonPropertyName("supplierId")] Guid SupplierId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);
