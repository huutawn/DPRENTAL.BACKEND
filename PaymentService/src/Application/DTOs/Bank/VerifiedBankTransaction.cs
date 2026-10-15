namespace Dprental.PaymentService.Application.DTOs.Bank;

public sealed record VerifiedBankTransaction(
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
    string? ReferenceCode,
    DateTimeOffset ProcessedAt);
