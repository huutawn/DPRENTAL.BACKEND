using Dprental.PaymentService.Common;

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
    DateTimeOffset VerifiedAt)
{
    public static Result<VerifiedWriteContext> FromResponse(TransactionVerificationResponse response, string purpose, DateTimeOffset now)
    {
        var body = response.Receipt?.Body;
        if (body is null || !body.IsUsable || body.IntegrityStatus != IntegrityStatuses.Valid ||
            !string.Equals(body.Purpose, purpose, StringComparison.Ordinal) || body.ExpiresAt <= now)
            return Error.Conflict("Payment.EvidenceNotUsable", "Confirmed, unexpired evidence is required before writing.");
        return new VerifiedWriteContext(body.ReceiptId, body.KeyId, body.ExpiresAt,
            body.EntityClaims.Select(x => new VerifiedWriteClaim(x.EntityType, x.EntityId, x.Revision, x.Digest)).ToArray(), now);
    }

    public Result Validate(DateTimeOffset now, params VerifiedWriteClaim[] required)
    {
        if (now >= ExpiresAt)
            return Error.Conflict("Payment.ReceiptExpired", "Verification receipt expired before commit.");
        foreach (var expected in required)
        {
            var claim = Claims.SingleOrDefault(x => x.EntityType == expected.EntityType && x.EntityId == expected.EntityId);
            if (claim is null || claim.Revision != expected.Revision ||
                !string.Equals(claim.Digest, expected.Digest, StringComparison.OrdinalIgnoreCase))
                return Error.Conflict("Payment.VerifiedSnapshotChanged", "Verified snapshot changed or is missing under the write lock.");
        }
        return Result.Success();
    }
}
