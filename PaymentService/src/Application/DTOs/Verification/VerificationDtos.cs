namespace Dprental.PaymentService.Application.DTOs.Verification;

public static class VerificationPurposes
{
    public const string PaymentConfirmation = "PAYMENT_CONFIRMATION";
    public const string Settlement = "SETTLEMENT";
    public const string ManualRefundConfirmation = "MANUAL_REFUND_CONFIRMATION";
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
    string? RequirementRef = null);

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

public sealed record VerificationReceipt(
    Guid ReceiptId,
    string KeyId,
    string RequestHash,
    string CallerNonce,
    string IntegrityStatus,
    bool IsUsable,
    string Reason,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Signature);

public sealed record TransactionVerificationResponse(
    TransactionIntegrityResult Integrity,
    TransactionUsabilityResult Usability,
    VerificationReceipt? Receipt = null);

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
