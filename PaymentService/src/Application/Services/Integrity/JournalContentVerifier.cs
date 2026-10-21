using System.Security.Cryptography;
using System.Text;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services.Integrity;

public sealed record JournalVerificationOutcome(
    bool IsValid,
    string? FailureReason,
    CanonicalJournalDocument? Document = null);

public static class JournalContentVerifier
{
    public static JournalVerificationOutcome Verify(
        TransactionJournalEntry entry,
        IPaymentDataEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(encryptor);

        // 1. Verify envelope digest binding
        if (string.IsNullOrWhiteSpace(entry.EncryptedPayload) ||
            string.IsNullOrWhiteSpace(entry.PayloadNonce) ||
            string.IsNullOrWhiteSpace(entry.PayloadTag) ||
            string.IsNullOrWhiteSpace(entry.EncryptionKeyId))
        {
            return new(false, "MISSING_ENCRYPTION_METADATA");
        }

        string computedEnvelopeDigest = CanonicalJournalCodec.ComputeEnvelopeDigest(
            entry.EncryptedPayload, entry.PayloadNonce, entry.PayloadTag, entry.EncryptionKeyId);

        if (!string.Equals(entry.EnvelopeDigest, computedEnvelopeDigest, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "ENVELOPE_DIGEST_MISMATCH");
        }

        // 2. Decrypt payload with AAD (Schema 4 primary, fallback to historical Schema 3 if needed)
        string canonicalJson;
        try
        {
            byte[] aad = CanonicalJournalCodec.ComputeJournalAad(4, "production", entry.StreamId, entry.Version, entry.EventId);
            byte[] decrypted;
            try
            {
                decrypted = encryptor.Decrypt(
                    entry.EncryptedPayload, entry.PayloadNonce, entry.PayloadTag, entry.EncryptionKeyId, aad);
            }
            catch (CryptographicException)
            {
                byte[] legacyAad = Encoding.UTF8.GetBytes($"{entry.StreamId}:{entry.Version}:{entry.EventId:D}");
                decrypted = encryptor.Decrypt(
                    entry.EncryptedPayload, entry.PayloadNonce, entry.PayloadTag, entry.EncryptionKeyId, legacyAad);
            }
            canonicalJson = Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return new(false, "DECRYPTION_FAILED");
        }
        catch (Exception ex)
        {
            return new(false, $"DECRYPTION_ERROR: {ex.Message}");
        }

        // 3. Compute payload hash from decrypted canonical JSON
        string computedPayloadHash = CanonicalJournalCodec.ComputePayloadHash(canonicalJson);
        if (!string.Equals(entry.PayloadHash, computedPayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "PAYLOAD_HASH_MISMATCH");
        }

        // 4. Parse canonical JSON into CanonicalJournalDocument
        CanonicalJournalDocument doc;
        try
        {
            doc = CanonicalJournalCodec.DeserializeFromCanonicalJson(canonicalJson);
        }
        catch (Exception ex)
        {
            return new(false, $"CANONICAL_PARSE_ERROR: {ex.Message}");
        }

        // 5. Cross-check metadata & index columns with decrypted document
        if (entry.Amount != doc.Amount ||
            !string.Equals(entry.Currency, doc.Currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Status, doc.Status, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.StreamId, doc.StreamId, StringComparison.Ordinal) ||
            entry.Version != doc.Version ||
            entry.EventId != doc.EventId ||
            !string.Equals(entry.EventType, doc.EventType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.ConfirmationSource, doc.ConfirmationSource, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Actor, doc.Actor, StringComparison.Ordinal) ||
            !string.Equals(entry.PreviousHash, doc.PreviousHash, StringComparison.OrdinalIgnoreCase) ||
            entry.OperationId != doc.OperationId ||
            !string.Equals(entry.StreamType, doc.StreamType, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "METADATA_MISMATCH");
        }

        if (!string.Equals(entry.OutboxDigest ?? string.Empty, doc.OutboxDigest ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
            entry.OutboxMessageId != doc.OutboxMessageId)
        {
            return new(false, "OUTBOX_LINK_MISMATCH");
        }

        if (doc.RelatedEntities is not null)
        {
            if (doc.RelatedEntities.TryGetValue("paymentRef", out var relPayRef) && !string.IsNullOrWhiteSpace(relPayRef) &&
                !string.Equals(entry.PaymentRef, relPayRef, StringComparison.Ordinal))
            {
                return new(false, "RELATION_PAYMENT_REF_MISMATCH");
            }
            if (doc.RelatedEntities.TryGetValue("invoiceRef", out var relInvRef) && !string.IsNullOrWhiteSpace(relInvRef) &&
                !string.Equals(entry.InvoiceRef, relInvRef, StringComparison.Ordinal))
            {
                return new(false, "RELATION_INVOICE_REF_MISMATCH");
            }
        }

        // 6. Recalculate entry hash
        string computedEntryHash = CanonicalJournalCodec.ComputeEntryHash(
            entry.StreamId, entry.Version, entry.EventId, entry.PreviousHash, entry.PayloadHash, entry.EnvelopeDigest);

        if (!string.Equals(entry.EntryHash, computedEntryHash, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "ENTRY_HASH_MISMATCH");
        }

        return new(true, null, doc);
    }
}
