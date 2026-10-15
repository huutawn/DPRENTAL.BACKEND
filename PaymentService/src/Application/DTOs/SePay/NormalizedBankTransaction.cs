namespace Dprental.PaymentService.Application.DTOs.SePay;

internal sealed record NormalizedBankTransaction(
    long Id,
    string Gateway,
    DateTimeOffset TransactionDate,
    string AccountNumber,
    string? SubAccount,
    string? Code,
    string? Content,
    string TransferType,
    string? Description,
    decimal TransferAmount,
    decimal? Accumulated,
    string? ReferenceCode);
