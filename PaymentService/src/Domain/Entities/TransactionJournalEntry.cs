namespace Dprental.PaymentService.Domain.Entities;

public sealed class TransactionJournalEntry
{
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public string StreamId { get; private set; } = string.Empty;
    public string StreamType { get; private set; } = string.Empty;
    public long Version { get; private set; }
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "VND";
    public string Status { get; private set; } = string.Empty;
    public string ConfirmationSource { get; private set; } = string.Empty;
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; private set; }
    public string? PaymentRef { get; private set; }
    public string? InvoiceRef { get; private set; }
    public Guid? RentalOrderId { get; private set; }
    public Guid? RentalItemId { get; private set; }
    public string? OutboxDigest { get; private set; }
    public Guid? OutboxMessageId { get; private set; }
    public string CanonicalPayload { get; private set; } = string.Empty;
    public string PreviousHash { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string EntryHash { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public string EncryptedPayload { get; private set; } = string.Empty;
    public string PayloadNonce { get; private set; } = string.Empty;
    public string PayloadTag { get; private set; } = string.Empty;
    public string EncryptionKeyId { get; private set; } = string.Empty;
    public bool IsConfirmed { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public string? ProofSignature { get; private set; }
    public string? SigningKeyId { get; private set; }

    private TransactionJournalEntry()
    {
    }

    public static TransactionJournalEntry Create(
        Guid operationId,
        string streamId,
        string streamType,
        long version,
        Guid eventId,
        string eventType,
        decimal amount,
        string currency,
        string status,
        string confirmationSource,
        string actor,
        DateTimeOffset timestampUtc,
        string canonicalPayload,
        string previousHash,
        string payloadHash,
        string entryHash,
        string fingerprint,
        string? paymentRef = null,
        string? invoiceRef = null,
        Guid? rentalOrderId = null,
        Guid? rentalItemId = null,
        string? outboxDigest = null,
        Guid? outboxMessageId = null,
        string encryptedPayload = "",
        string payloadNonce = "",
        string payloadTag = "",
        string encryptionKeyId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamType);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryHash);

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be >= 1.");
        }

        return new TransactionJournalEntry
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            StreamId = streamId.Trim(),
            StreamType = streamType.Trim(),
            Version = version,
            EventId = eventId,
            EventType = eventType.Trim(),
            Amount = amount,
            Currency = currency.Trim().ToUpperInvariant(),
            Status = status.Trim().ToUpperInvariant(),
            ConfirmationSource = confirmationSource.Trim().ToUpperInvariant(),
            Actor = actor.Trim(),
            TimestampUtc = timestampUtc.ToUniversalTime(),
            PaymentRef = paymentRef?.Trim(),
            InvoiceRef = invoiceRef?.Trim(),
            RentalOrderId = rentalOrderId,
            RentalItemId = rentalItemId,
            OutboxDigest = outboxDigest?.Trim(),
            OutboxMessageId = outboxMessageId,
            CanonicalPayload = canonicalPayload,
            PreviousHash = previousHash.Trim().ToLowerInvariant(),
            PayloadHash = payloadHash.Trim().ToLowerInvariant(),
            EntryHash = entryHash.Trim().ToLowerInvariant(),
            Fingerprint = fingerprint.Trim(),
            EncryptedPayload = encryptedPayload ?? string.Empty,
            PayloadNonce = payloadNonce ?? string.Empty,
            PayloadTag = payloadTag ?? string.Empty,
            EncryptionKeyId = encryptionKeyId ?? string.Empty,
            IsConfirmed = false,
            ConfirmedAt = null,
            ProofSignature = null,
            SigningKeyId = null
        };
    }

    public void Confirm(string proofSignature, string signingKeyId, DateTimeOffset confirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proofSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKeyId);

        IsConfirmed = true;
        ProofSignature = proofSignature.Trim();
        SigningKeyId = signingKeyId.Trim();
        ConfirmedAt = confirmedAt.ToUniversalTime();
    }
}
