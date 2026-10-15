using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.Domain.Entities;

[Table("payment_transactions")]
public sealed class PaymentTransaction
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(32)]
    public string Provider { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string ProviderTransactionId { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string PaymentRef { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Gateway { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string AccountNumber { get; private set; } = string.Empty;

    [MaxLength(64)]
    public string? SubAccount { get; private set; }

    [Required]
    [MaxLength(16)]
    public string TransferType { get; private set; } = string.Empty;

    [Required]
    [Column(TypeName = "numeric(18,0)")]
    public decimal TransferAmount { get; private set; }

    [Column(TypeName = "numeric(18,0)")]
    public decimal? Accumulated { get; private set; }

    [Required]
    public DateTimeOffset TransactionDate { get; private set; }

    [MaxLength(128)]
    public string? ReferenceCode { get; private set; }

    [MaxLength(1000)]
    public string? Description { get; private set; }

    [MaxLength(1000)]
    public string? Content { get; private set; }

    [Column(TypeName = "text")]
    public string? ProviderCode { get; private set; }

    [MaxLength(32)]
    public string? PaymentCode { get; private set; }

    public Guid? InvoiceId { get; private set; }

    [MaxLength(64)]
    public string? InvoiceRef { get; private set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; private set; } = string.Empty;

    [Required]
    public DateTimeOffset CreatedAt { get; private set; }

    private PaymentTransaction()
    {
    }

    public static PaymentTransaction Create(
        string provider,
        string providerTransactionId,
        string paymentRef,
        string gateway,
        string accountNumber,
        string? subAccount,
        string transferType,
        decimal transferAmount,
        decimal? accumulated,
        DateTimeOffset transactionDate,
        string? referenceCode,
        string? description,
        string? content,
        string? providerCode,
        string? paymentCode,
        Guid? invoiceId,
        string? invoiceRef,
        string status,
        string reasonCode,
        DateTimeOffset createdAt)
    {
        return new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            ProviderTransactionId = providerTransactionId,
            PaymentRef = paymentRef,
            Gateway = gateway,
            AccountNumber = accountNumber,
            SubAccount = subAccount,
            TransferType = transferType,
            TransferAmount = transferAmount,
            Accumulated = accumulated,
            TransactionDate = transactionDate.ToUniversalTime(),
            ReferenceCode = referenceCode,
            Description = description,
            Content = content,
            ProviderCode = providerCode,
            PaymentCode = paymentCode,
            InvoiceId = invoiceId,
            InvoiceRef = invoiceRef,
            Status = status,
            ReasonCode = reasonCode,
            CreatedAt = createdAt.ToUniversalTime()
        };
    }

    public bool MatchesBusinessData(
        string gateway,
        string accountNumber,
        string transferType,
        decimal transferAmount,
        DateTimeOffset transactionDate,
        string? referenceCode,
        string? content,
        string? providerCode)
    {
        return string.Equals(Gateway, gateway, StringComparison.OrdinalIgnoreCase)
            && string.Equals(AccountNumber, accountNumber, StringComparison.Ordinal)
            && string.Equals(TransferType, transferType, StringComparison.OrdinalIgnoreCase)
            && TransferAmount == transferAmount
            && Math.Abs((TransactionDate - transactionDate.ToUniversalTime()).TotalMilliseconds) < 1
            && string.Equals(ReferenceCode ?? string.Empty, referenceCode ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(Content ?? string.Empty, content ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(ProviderCode, providerCode, StringComparison.Ordinal);
    }
}
