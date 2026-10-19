namespace Dprental.PaymentService.Application.DTOs.Verification;

public sealed record VerifiedWriteClaim(
    string EntityType,
    string EntityId,
    long Revision,
    string Digest);

public sealed record VerifiedWriteContext(
    Guid ReceiptId,
    string KeyId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<VerifiedWriteClaim> Claims,
    DateTimeOffset VerifiedAt);
