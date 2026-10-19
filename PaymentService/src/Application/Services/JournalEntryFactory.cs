using System.Text;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

public static class JournalEntryFactory
{
    public static TransactionJournalEntry CreateEntry(
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
        string previousHash,
        DateTimeOffset? bankTimeUtc = null,
        IReadOnlyDictionary<string, string?>? relatedEntities = null,
        IReadOnlyDictionary<string, object?>? snapshot = null,
        string? outboxDigest = null,
        Guid? outboxMessageId = null,
        string? paymentRef = null,
        string? invoiceRef = null,
        Guid? rentalOrderId = null,
        Guid? rentalItemId = null,
        IPaymentDataEncryptor? encryptor = null,
        string environment = "production")
    {
        var activeEncryptor = encryptor ?? new PaymentDataEncryptor(new byte[32]);

        var doc = new CanonicalJournalDocument(
            SchemaVersion: CanonicalJournalCodec.CurrentSchemaVersion,
            Environment: environment,
            OperationId: operationId,
            EventId: eventId,
            StreamType: streamType,
            StreamId: streamId,
            Version: version,
            EventType: eventType,
            Amount: amount,
            Currency: currency,
            Status: status,
            ConfirmationSource: confirmationSource,
            Actor: actor,
            RecordedAtUtc: timestampUtc,
            PreviousHash: previousHash,
            BankTimeUtc: bankTimeUtc,
            OutboxDigest: outboxDigest,
            OutboxMessageId: outboxMessageId,
            RelatedEntities: relatedEntities,
            Snapshot: snapshot);

        string canonicalJson = CanonicalJournalCodec.SerializeToCanonicalJson(doc);
        string payloadHash = CanonicalJournalCodec.ComputePayloadHash(canonicalJson);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(canonicalJson);
        byte[] aad = Encoding.UTF8.GetBytes($"{streamId}:{version}:{eventId:D}");
        var encResult = activeEncryptor.Encrypt(plaintextBytes, aad);

        string envelopeDigest = CanonicalJournalCodec.ComputeEnvelopeDigest(
            encResult.CiphertextBase64, encResult.NonceBase64, encResult.TagBase64, encResult.KeyId);

        string entryHash = CanonicalJournalCodec.ComputeEntryHash(
            streamId, version, eventId, previousHash, payloadHash, envelopeDigest);

        string fingerprint = CanonicalJournalHasher.ComputeFingerprint(streamId, eventId, payloadHash);

        return TransactionJournalEntry.Create(
            operationId: operationId,
            streamId: streamId,
            streamType: streamType,
            version: version,
            eventId: eventId,
            eventType: eventType,
            amount: amount,
            currency: currency,
            status: status,
            confirmationSource: confirmationSource,
            actor: actor,
            timestampUtc: timestampUtc,
            canonicalPayload: string.Empty, // Plaintext not stored; only encrypted payload stored
            previousHash: previousHash,
            payloadHash: payloadHash,
            entryHash: entryHash,
            fingerprint: fingerprint,
            paymentRef: paymentRef,
            invoiceRef: invoiceRef,
            rentalOrderId: rentalOrderId,
            rentalItemId: rentalItemId,
            outboxDigest: outboxDigest,
            outboxMessageId: outboxMessageId,
            encryptedPayload: encResult.CiphertextBase64,
            payloadNonce: encResult.NonceBase64,
            payloadTag: encResult.TagBase64,
            encryptionKeyId: encResult.KeyId);
    }
}
