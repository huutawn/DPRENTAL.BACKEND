using System.Text.Json.Serialization;

namespace Dprental.PaymentService.Application.DTOs.Events;

public sealed record InvoicePaidFinancialPayload(
    [property: JsonPropertyName("invoiceId")] Guid InvoiceId,
    [property: JsonPropertyName("invoiceRef")] string InvoiceRef,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("renterId")] Guid RenterId,
    [property: JsonPropertyName("rentalAmount")] decimal RentalAmount,
    [property: JsonPropertyName("depositAmount")] decimal DepositAmount,
    [property: JsonPropertyName("totalAmount")] decimal TotalAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("paidAt")] DateTimeOffset PaidAt);
