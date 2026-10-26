using System.Text.Json.Serialization;

namespace Dprental.PaymentService.Application.DTOs.Events;

public sealed record RentalItemFinancialSettledPayload(
    [property: JsonPropertyName("settlementId")] Guid SettlementId,
    [property: JsonPropertyName("settlementRef")] string SettlementRef,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId,
    [property: JsonPropertyName("supplierId")] Guid SupplierId,
    [property: JsonPropertyName("rentalAmount")] decimal RentalAmount,
    [property: JsonPropertyName("depositAmount")] decimal DepositAmount,
    [property: JsonPropertyName("finalDeductionAmount")] decimal FinalDeductionAmount,
    [property: JsonPropertyName("depositRefundDue")] decimal DepositRefundDue,
    [property: JsonPropertyName("platformFeeRate")] decimal PlatformFeeRate,
    [property: JsonPropertyName("platformFeeAmount")] decimal PlatformFeeAmount,
    [property: JsonPropertyName("supplierPayable")] decimal SupplierPayable,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt);
