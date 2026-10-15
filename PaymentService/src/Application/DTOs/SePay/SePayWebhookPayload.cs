using System.Text.Json.Serialization;

namespace Dprental.PaymentService.Application.DTOs.SePay;

public sealed record SePayWebhookPayload(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("gateway")] string? Gateway,
    [property: JsonPropertyName("transactionDate")] string? TransactionDate,
    [property: JsonPropertyName("accountNumber")] string? AccountNumber,
    [property: JsonPropertyName("subAccount")] string? SubAccount,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("transferType")] string? TransferType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("transferAmount")] decimal? TransferAmount,
    [property: JsonPropertyName("accumulated")] decimal? Accumulated,
    [property: JsonPropertyName("referenceCode")] string? ReferenceCode);
