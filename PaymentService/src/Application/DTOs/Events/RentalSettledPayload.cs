using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Application.DTOs.Events;

/// <summary>
/// Chỉ tạo khi Payment đã đóng các nghĩa vụ tài chính bắt buộc cho RentalItem.
/// </summary>
public sealed record RentalSettledPayload(
    [property: JsonPropertyName("rentalOrderId")][Required] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")][Required] Guid RentalItemId,
    [property: JsonPropertyName("invoiceRef")][Required] string InvoiceRef,
    [property: JsonPropertyName("paymentRef")][Required] string PaymentRef,
    [property: JsonPropertyName("settlementRef")][Required] string SettlementRef,
    [property: JsonPropertyName("operationKey")][Required] string OperationKey,
    [property: JsonPropertyName("finalDeductionAmount")][Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal FinalDeductionAmount,
    [property: JsonPropertyName("refundedDepositAmount")][Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedDepositAmount,
    [property: JsonPropertyName("currency")][Required] string Currency,
    [property: JsonPropertyName("refundRef")] string? RefundRef,
    [property: JsonPropertyName("completedAt")][Required] DateTimeOffset CompletedAt);
