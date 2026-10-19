using System.Security.Cryptography;
using System.Text;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class TransactionJournalCryptographyTests
{
    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeTransactionJournalRepo(TransactionJournalEntry? entryToReturn = null) : ITransactionJournalRepository
    {
        public Task<TransactionJournalEntry?> GetLatestEntryForStreamAsync(string streamId, CancellationToken cancellationToken = default) =>
            Task.FromResult(entryToReturn);

        public Task<TransactionJournalEntry?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(entryToReturn?.EventId == eventId ? entryToReturn : null);

        public Task<TransactionJournalEntry?> GetByStreamAndVersionAsync(string streamId, long version, CancellationToken cancellationToken = default) =>
            Task.FromResult(entryToReturn);

        public Task<IReadOnlyList<TransactionJournalEntry>> GetStreamEntriesAsync(string streamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransactionJournalEntry>>(entryToReturn is not null ? [entryToReturn] : []);

        public Task<IReadOnlyList<TransactionJournalEntry>> GetPendingConfirmationEntriesAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransactionJournalEntry>>([]);

        public Task<TransactionJournalEntry?> GetByOutboxDigestAsync(string outboxDigest, CancellationToken cancellationToken = default) =>
            Task.FromResult(entryToReturn);

        public Task<IReadOnlyList<TransactionJournalEntry>> QueryHistoryAsync(
            string? streamId, string? paymentRef, string? invoiceRef, DateTimeOffset? cursorTimestamp, Guid? cursorId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TransactionJournalEntry>>(entryToReturn is not null ? [entryToReturn] : []);

        public Task AppendEntryAsync(TransactionJournalEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateConfirmationAsync(Guid entryId, string proofSignature, string signingKeyId, DateTimeOffset confirmedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public void AesGcmEncryption_RoundTrip_Succeeds()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        byte[] plaintext = Encoding.UTF8.GetBytes("SensitiveBankInfo:MBBank:0383799632:SecretAccount");
        byte[] aad = Encoding.UTF8.GetBytes("stream-1:1:uuid");

        var (ciphertext, nonce, tag) = AesGcmEncryption.Encrypt(key, plaintext, aad);

        Assert.Equal(plaintext.Length, ciphertext.Length);
        Assert.Equal(12, nonce.Length);
        Assert.Equal(16, tag.Length);

        byte[] decrypted = AesGcmEncryption.Decrypt(key, ciphertext, nonce, tag, aad);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcmEncryption_TamperedCiphertext_ThrowsCryptographicException()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        byte[] plaintext = Encoding.UTF8.GetBytes("SensitiveData");
        byte[] aad = Encoding.UTF8.GetBytes("aad");

        var (ciphertext, nonce, tag) = AesGcmEncryption.Encrypt(key, plaintext, aad);

        ciphertext[0] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmEncryption.Decrypt(key, ciphertext, nonce, tag, aad));
    }

    [Fact]
    public void AesGcmEncryption_TamperedAad_ThrowsCryptographicException()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        byte[] plaintext = Encoding.UTF8.GetBytes("SensitiveData");
        byte[] aad = Encoding.UTF8.GetBytes("aad-original");

        var (ciphertext, nonce, tag) = AesGcmEncryption.Encrypt(key, plaintext, aad);

        byte[] tamperedAad = Encoding.UTF8.GetBytes("aad-tampered");

        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmEncryption.Decrypt(key, ciphertext, nonce, tag, tamperedAad));
    }

    [Fact]
    public void CanonicalJournalHasher_PropertyOrderInvariance_ProducesIdenticalHash()
    {
        DateTimeOffset now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

        var dict1 = new Dictionary<string, string?>
        {
            ["z"] = "last",
            ["a"] = "first",
            ["m"] = "middle"
        };

        var dict2 = new Dictionary<string, string?>
        {
            ["a"] = "first",
            ["m"] = "middle",
            ["z"] = "last"
        };

        string hash1 = CanonicalJournalHasher.ComputePayloadHash(
            500_000m, "VND", "PAID", "WEBHOOK", now, "SEPAY", dict1, "outbox123");

        string hash2 = CanonicalJournalHasher.ComputePayloadHash(
            500_000m, "VND", "PAID", "WEBHOOK", now, "SEPAY", dict2, "outbox123");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void CanonicalJournalHasher_AmountOrStatusChange_ProducesDifferentHash()
    {
        DateTimeOffset now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

        string originalHash = CanonicalJournalHasher.ComputePayloadHash(
            500_000m, "VND", "PAID", "WEBHOOK", now, "SEPAY", null, null);

        string tamperedAmountHash = CanonicalJournalHasher.ComputePayloadHash(
            500_001m, "VND", "PAID", "WEBHOOK", now, "SEPAY", null, null);

        string tamperedStatusHash = CanonicalJournalHasher.ComputePayloadHash(
            500_000m, "VND", "CANCELLED", "WEBHOOK", now, "SEPAY", null, null);

        Assert.NotEqual(originalHash, tamperedAmountHash);
        Assert.NotEqual(originalHash, tamperedStatusHash);
    }

    [Fact]
    public void EcdsaAttestation_SignAndVerify_Succeeds()
    {
        using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string message = "DPRENTAL:V1:k1:SEPAY:1001:1:guid:entryhash:2026-10-19T10:00:00Z";

        string signature = EcdsaAttestation.Sign(ecdsa, message);
        Assert.False(string.IsNullOrWhiteSpace(signature));

        bool valid = EcdsaAttestation.Verify(ecdsa, message, signature);
        Assert.True(valid);

        bool tampered = EcdsaAttestation.Verify(ecdsa, message + "-tampered", signature);
        Assert.False(tampered);
    }

    [Fact]
    public void EcdsaAttestation_KeyExportImport_RoundTrip()
    {
        using var original = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string publicSpki = EcdsaAttestation.ExportPublicKeyBase64(original);
        string privatePkcs8 = EcdsaAttestation.ExportPrivateKeyBase64(original);

        using var importedPublic = EcdsaAttestation.ImportPublicKeyBase64(publicSpki);
        using var importedPrivate = EcdsaAttestation.ImportPrivateKeyBase64(privatePkcs8);

        string message = "test-message";
        string sig = EcdsaAttestation.Sign(importedPrivate, message);
        bool ok = EcdsaAttestation.Verify(importedPublic, message, sig);

        Assert.True(ok);
    }

    [Fact]
    public async Task IntegrityHostClient_ValidReceipt_VerifiesSuccessfully()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(signingKey);
        string keyId = "key-v1";

        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.AddSeconds(5);

        var req = new TransactionVerificationRequest(
            PaymentRef: "SEPAY:999",
            Purpose: VerificationPurposes.PaymentConfirmation,
            CallerNonce: "nonce-123",
            ExpectedAmount: 500_000m,
            ExpectedCurrency: "VND");

        string requestHash = CanonicalJournalHasher.ComputeRequestHash(
            req.PaymentRef, req.Purpose, req.CallerNonce, req.ExpectedAmount, req.ExpectedCurrency);

        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessage(
            keyId, requestHash, req.CallerNonce, IntegrityStatuses.Valid, true, "OK", now, expiresAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        var expectedResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult(IntegrityStatuses.Valid, 1, "WEBHOOK", now, keyId, sig, "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            new VerificationReceipt(Guid.NewGuid(), keyId, requestHash, req.CallerNonce, IntegrityStatuses.Valid, true, "OK", now, expiresAt, sig));

        var handler = new FakeHttpHandler(expectedResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://integrity-host:8081") };

        var client = new IntegrityHostClient(httpClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrityHostClient>.Instance);
        var result = await client.VerifyAsync(req);

        Assert.True(result.IsSuccess);
        Assert.Equal(IntegrityStatuses.Valid, result.Value.Integrity.Status);
        Assert.True(result.Value.Usability.IsUsable);
        Assert.NotNull(result.Value.Receipt);
        Assert.Equal(sig, result.Value.Receipt.Signature);
    }

    [Fact]
    public async Task IntegrityHostClient_ExpiredReceipt_FailsWithConflict()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(signingKey);
        string keyId = "key-v1";

        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 }
        });

        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset expiredAt = past.AddSeconds(5); // Expired!

        var req = new TransactionVerificationRequest(
            PaymentRef: "SEPAY:888",
            Purpose: VerificationPurposes.PaymentConfirmation,
            CallerNonce: "nonce-456");

        string requestHash = CanonicalJournalHasher.ComputeRequestHash(
            req.PaymentRef, req.Purpose, req.CallerNonce);

        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessage(
            keyId, requestHash, req.CallerNonce, IntegrityStatuses.Valid, true, "OK", past, expiredAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        var expiredResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult(IntegrityStatuses.Valid, 1, "WEBHOOK", past, keyId, sig, "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 300_000m, "VND", "PAID"),
            new VerificationReceipt(Guid.NewGuid(), keyId, requestHash, req.CallerNonce, IntegrityStatuses.Valid, true, "OK", past, expiredAt, sig));

        var handler = new FakeHttpHandler(expiredResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://integrity-host:8081") };

        var client = new IntegrityHostClient(httpClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<IntegrityHostClient>.Instance);
        var result = await client.VerifyAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("IntegrityHost.ReceiptExpired", result.Error.Code);
    }

    private sealed class FakeHttpHandler(TransactionVerificationResponse response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(response)
            };
            return Task.FromResult(msg);
        }
    }
}
