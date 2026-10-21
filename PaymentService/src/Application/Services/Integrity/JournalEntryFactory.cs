using System.Text;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services.Integrity;

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
        IPaymentDataEncryptor encryptor,
        DateTimeOffset? bankTimeUtc = null,
        IReadOnlyDictionary<string, string?>? relatedEntities = null,
        IReadOnlyDictionary<string, object?>? snapshot = null,
        IReadOnlyList<CanonicalEntityClaim>? entityClaims = null,
        string? outboxDigest = null,
        Guid? outboxMessageId = null,
        string? paymentRef = null,
        string? invoiceRef = null,
        Guid? rentalOrderId = null,
        Guid? rentalItemId = null,
        string environment = "production")
    {
        ArgumentNullException.ThrowIfNull(encryptor);

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
            Snapshot: snapshot,
            EntityClaims: entityClaims);

        string canonicalJson = CanonicalJournalCodec.SerializeToCanonicalJson(doc);
        string payloadHash = CanonicalJournalCodec.ComputePayloadHash(canonicalJson);

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(canonicalJson);
        byte[] aad = CanonicalJournalCodec.ComputeJournalAad(CanonicalJournalCodec.CurrentSchemaVersion, environment, streamId, version, eventId);
        var encResult = encryptor.Encrypt(plaintextBytes, aad);

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
            envelopeDigest: envelopeDigest,
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
    public static async Task<TransactionJournalEntry> CreateNextAsync(
        Dprental.PaymentService.Domain.IRepositories.IInvoiceRepository repository,
        Guid operationId, string streamId, string streamType, string eventType,
        Invoice invoice, decimal amount, string status, string actor, DateTimeOffset now,
        IReadOnlyList<CanonicalEntityClaim> claims, IPaymentDataEncryptor encryptor,
        OutboxMessage? message, CancellationToken ct)
    {
        var previous = await repository.GetLatestJournalEntryAsync(streamId, ct);
        Guid eventId = Guid.NewGuid();
        message?.LinkAttestation(eventId, operationId);
        return CreateEntry(operationId, streamId, streamType, (previous?.Version ?? 0) + 1,
            eventId, eventType, amount, invoice.Currency, status, JournalConfirmationSources.System,
            actor, now, previous?.EntryHash ?? CanonicalJournalHasher.GenesisHash, encryptor,
            relatedEntities: new Dictionary<string, string?>
            {
                ["invoiceRef"] = invoice.InvoiceRef, ["paymentRef"] = invoice.PaymentRef,
                ["rentalOrderId"] = invoice.RentalOrderId.ToString("D")
            }, entityClaims: claims, outboxDigest: message is null ? null : CanonicalJournalHasher.ComputeOutboxDigest(message.Payload),
            outboxMessageId: message?.MessageId, paymentRef: invoice.PaymentRef,
            invoiceRef: invoice.InvoiceRef, rentalOrderId: invoice.RentalOrderId);
    }

}
