namespace Dprental.PaymentService.Application.DTOs.Verification;

public static class VerificationPurposes
{
    public const string PaymentConfirmation = "PAYMENT_CONFIRMATION";
    public const string RefundRequest = "REFUND_REQUEST";
    public const string Settlement = "SETTLEMENT";
    public const string ManualRefundConfirmation = "MANUAL_REFUND_CONFIRMATION";
    public const string OutboxPublication = "OUTBOX_PUBLICATION";
    public const string LatePaymentAcceptance = "LATE_PAYMENT_ACCEPTANCE";
}

public sealed record VerificationEntityClaim(
    string EntityType,
    string EntityId,
    long Revision,
    string Digest,
    string? SourceProof = null);

public sealed record VerificationReceiptBody(
    int Schema,
    string Issuer,
    string Audience,
    string Environment,
    Guid ReceiptId,
    string KeyId,
    string RequestHash,
    string CallerIdentity,
    string CallerNonce,
    string Purpose,
    string IntegrityStatus,
    bool IsUsable,
    string Reason,
    decimal ActualAmount,
    string ActualCurrency,
    string ActualStatus,
    IReadOnlyList<VerificationEntityClaim> EntityClaims,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record VerificationReceipt(
    VerificationReceiptBody Body,
    string Signature)
{
    public Guid ReceiptId => Body.ReceiptId;
    public string KeyId => Body.KeyId;
    public string RequestHash => Body.RequestHash;
    public string CallerIdentity => Body.CallerIdentity;
    public string CallerNonce => Body.CallerNonce;
    public string IntegrityStatus => Body.IntegrityStatus;
    public bool IsUsable => Body.IsUsable;
    public string Reason => Body.Reason;
    public DateTimeOffset IssuedAt => Body.IssuedAt;
    public DateTimeOffset ExpiresAt => Body.ExpiresAt;
}

public sealed record TransactionVerificationRequest(
    string PaymentRef,
    string Purpose,
    string CallerNonce,
    decimal? ExpectedAmount = null,
    string? ExpectedCurrency = null,
    string? ExpectedStatus = null,
    string? InvoiceRef = null,
    Guid? ExpectedOrderId = null,
    Guid? ExpectedRenterId = null,
    Guid? RentalItemId = null,
    Guid? SupplierId = null,
    string? RequirementRef = null,
    string? CallerIdentity = null,
    string? ExpectedOutboxDigest = null,
    string? Topic = null,
    string? MessageKey = null,
    Guid? OutboxMessageId = null);

public sealed record TransactionIntegrityResult(
    string Status, // VALID, PENDING_CONFIRMATION, TAMPERED, NOT_FOUND
    long Version,
    string? ConfirmationSource,
    DateTimeOffset? ConfirmedAt,
    string? KeyId,
    string? Signature,
    string? Proof,
    string CallerNonce);

public sealed record TransactionUsabilityResult(
    bool IsUsable,
    string Reason,
    decimal ActualAmount,
    string ActualCurrency,
    string ActualStatus);

public sealed record TransactionVerificationResponse(
    TransactionIntegrityResult Integrity,
    TransactionUsabilityResult Usability,
    VerificationReceipt? Receipt = null,
    Dprental.PaymentService.Common.Cryptography.PublicationProof? PublicationProof = null);

public static class IntegrityStatuses
{
    public const string Valid = "VALID";
    public const string PendingConfirmation = "PENDING_CONFIRMATION";
    public const string Tampered = "TAMPERED";
    public const string NotFound = "NOT_FOUND";
}

public sealed record JournalHistoryItemDto(
    Guid Id,
    string StreamId,
    string StreamType,
    long Version,
    Guid EventId,
    string EventType,
    decimal Amount,
    string Currency,
    string Status,
    string ConfirmationSource,
    string Actor,
    DateTimeOffset TimestampUtc,
    string? PaymentRef,
    string? InvoiceRef,
    Guid? RentalOrderId,
    Guid? RentalItemId,
    bool IsConfirmed,
    DateTimeOffset? ConfirmedAt,
    string? ProofSignature,
    string? SigningKeyId,
    string Fingerprint,
    string EntryHash);

public sealed record CursorPaginatedHistoryResponse(
    IReadOnlyList<JournalHistoryItemDto> Items,
    string? NextCursor,
    bool HasMore);
