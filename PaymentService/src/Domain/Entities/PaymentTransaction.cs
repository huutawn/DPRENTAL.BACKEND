using Dprental.PaymentService.Domain.Events;

namespace Dprental.PaymentService.Domain.Entities;

public sealed class PaymentTransaction
{
    private readonly List<IFinancialDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string ProviderTransactionId { get; private set; } = string.Empty;
    public string PaymentRef { get; private set; } = string.Empty;
    public string Gateway { get; private set; } = string.Empty;
    public string AccountNumber { get; private set; } = string.Empty;
    public string? SubAccount { get; private set; }
    public string TransferType { get; private set; } = string.Empty;
    public decimal TransferAmount { get; private set; }
    public decimal? Accumulated { get; private set; }
    public DateTimeOffset TransactionDate { get; private set; }
    public string? ReferenceCode { get; private set; }
    public string? Description { get; private set; }
    public string? Content { get; private set; }
    public string? ProviderCode { get; private set; }
    public string? PaymentCode { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public string? InvoiceRef { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string ReasonCode { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public long Revision { get; private set; } = 1;

    public IReadOnlyList<IFinancialDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();

    internal void SetDecryptedSensitiveFields(string accountNumber, string? subAccount, string? content)
    {
        AccountNumber = accountNumber;
        SubAccount = subAccount;
        Content = content;
    }

    public void EnsureDecrypted(Dprental.PaymentService.Common.Cryptography.IPaymentDataEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);
        if (Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.IsEncrypted(AccountNumber))
        {
            string idStr = Id.ToString("D");
            string decAccount = Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Decrypt(
                encryptor, "PaymentTransaction", idStr, "AccountNumber", AccountNumber) ?? string.Empty;
            string? decSub = Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Decrypt(
                encryptor, "PaymentTransaction", idStr, "SubAccount", SubAccount);
            string? decContent = Dprental.PaymentService.Common.Cryptography.SensitiveFieldEncryptor.Decrypt(
                encryptor, "PaymentTransaction", idStr, "Content", Content);
            SetDecryptedSensitiveFields(decAccount, decSub, decContent);
        }
    }

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
        var pt = new PaymentTransaction
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

        pt._domainEvents.Add(new BankTransactionReceivedDomainEvent(
            pt.Id,
            pt.Provider,
            pt.ProviderTransactionId,
            pt.PaymentRef,
            pt.TransferType,
            pt.TransferAmount,
            pt.Gateway,
            pt.AccountNumber,
            pt.PaymentCode,
            pt.ReferenceCode,
            pt.Status,
            pt.ReasonCode,
            pt.InvoiceId,
            pt.InvoiceRef,
            pt.TransactionDate,
            pt.CreatedAt));

        return pt;
    }

    public bool MatchesBusinessData(
        string gateway,
        string accountNumber,
        string? subAccount,
        string transferType,
        decimal transferAmount,
        DateTimeOffset transactionDate,
        string? referenceCode,
        string? description,
        string? content,
        string? providerCode)
    {
        return string.Equals(Gateway, gateway, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(AccountNumber, accountNumber, StringComparison.Ordinal) &&
               string.Equals(SubAccount, subAccount, StringComparison.Ordinal) &&
               string.Equals(TransferType, transferType, StringComparison.OrdinalIgnoreCase) &&
               TransferAmount == transferAmount &&
               TransactionDate == transactionDate.ToUniversalTime() &&
               string.Equals(ReferenceCode, referenceCode, StringComparison.Ordinal) &&
               string.Equals(Description, description, StringComparison.Ordinal) &&
               string.Equals(Content, content, StringComparison.Ordinal) &&
               string.Equals(ProviderCode, providerCode, StringComparison.Ordinal);
    }

    public void AcceptLatePayment(string reasonCode = PaymentTransactionReason.LatePaymentAccepted)
    {
        Status = PaymentTransactionStatus.Matched;
        ReasonCode = reasonCode;
        Revision++;
    }
}
