using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Clients;
using Dprental.PaymentService.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class IntegrityBlockerRegressionTests
{
    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MockHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    [Fact]
    public async Task IntegrityHostClient_ResponseWithNullReceipt_MustBeRejected()
    {
        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = "k1",
            PublicKeys = new Dictionary<string, string> { ["k1"] = "base64" }
        });

        // Backend returns response without signed VerificationReceipt
        var unsignedResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", DateTimeOffset.UtcNow, "k1", "sig", "proof", "nonce-1"),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            Receipt: null); // Null receipt!

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(unsignedResponse)
        };

        using var httpClient = new HttpClient(new MockHttpHandler(httpResponse))
        {
            BaseAddress = new Uri("http://localhost:8081")
        };

        var client = new IntegrityHostClient(httpClient, options, NullLogger<IntegrityHostClient>.Instance);

        var req = new TransactionVerificationRequest(
            PaymentRef: "PAY-1",
            Purpose: VerificationPurposes.PaymentConfirmation,
            CallerNonce: "nonce-1");

        var result = await client.VerifyAsync(req);

        // Blocker 1: Must be rejected because unsigned response cannot be trusted
        Assert.True(result.IsFailure);
        Assert.Contains("Receipt", result.Error.Code, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task IntegrityHostClient_TamperedOuterUsability_BindsToReceiptBody()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string pubKey = EcdsaAttestation.ExportPublicKeyBase64(signingKey);
        string keyId = "test-k1";

        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = pubKey }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.AddSeconds(4);

        var req = new TransactionVerificationRequest(
            PaymentRef: "PAY-1",
            Purpose: VerificationPurposes.PaymentConfirmation,
            CallerNonce: "nonce-1");

        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, req.CallerNonce);

        // Inside signed receipt: IsUsable = false!
        var body = new VerificationReceiptBody(
            Schema: 2,
            Issuer: "dprental-integrity-host",
            Audience: "payment-api",
            Environment: "production",
            ReceiptId: Guid.NewGuid(),
            KeyId: keyId,
            RequestHash: requestHash,
            CallerIdentity: "",
            CallerNonce: req.CallerNonce,
            Purpose: req.Purpose,
            IntegrityStatus: IntegrityStatuses.Valid,
            IsUsable: false, // Inside receipt is false!
            Reason: "INVOICE_NOT_PAID",
            ActualAmount: 500_000m,
            ActualCurrency: "VND",
            ActualStatus: "PAID",
            EntityClaims: [],
            IssuedAt: now,
            ExpiresAt: expiresAt);

        string claimsDigest = EcdsaAttestation.ComputeClaimsDigest([]);
        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessageV2(
            body.Schema, body.Issuer, body.Audience, body.Environment, body.ReceiptId, body.KeyId,
            body.RequestHash, body.CallerIdentity, body.CallerNonce, body.Purpose, body.IntegrityStatus,
            body.IsUsable, body.Reason, body.ActualAmount, body.ActualCurrency, body.ActualStatus,
            claimsDigest, body.IssuedAt, body.ExpiresAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        // Attacker alters outer Usability to true
        var tamperedResponse = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", now, keyId, sig, "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"), // Outer tampered to true!
            new VerificationReceipt(body, sig));
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(tamperedResponse)
        };

        using var httpClient = new HttpClient(new MockHttpHandler(httpResponse)) { BaseAddress = new Uri("http://localhost:8081") };
        var client = new IntegrityHostClient(httpClient, options, NullLogger<IntegrityHostClient>.Instance);

        var result = await client.VerifyAsync(req);

        // Client binds to verified receipt body, ignoring the tampered outer field
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Usability.IsUsable);
        Assert.Equal("INVOICE_NOT_PAID", result.Value.Usability.Reason);
    }

    [Fact]
    public async Task IntegrityHostClient_TamperedSignature_ReturnsConflict()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string pubKey = EcdsaAttestation.ExportPublicKeyBase64(signingKey);
        string keyId = "test-k1";

        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = pubKey }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.AddSeconds(4);

        var req = new TransactionVerificationRequest(PaymentRef: "PAY-1", Purpose: VerificationPurposes.PaymentConfirmation, CallerNonce: "nonce-1");
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, req.CallerNonce);

        var body = new VerificationReceiptBody(
            Schema: 2,
            Issuer: "dprental-integrity-host",
            Audience: "payment-api",
            Environment: "production",
            ReceiptId: Guid.NewGuid(),
            KeyId: keyId,
            RequestHash: requestHash,
            CallerIdentity: "",
            CallerNonce: req.CallerNonce,
            Purpose: req.Purpose,
            IntegrityStatus: IntegrityStatuses.Valid,
            IsUsable: true,
            Reason: "OK",
            ActualAmount: 500_000m,
            ActualCurrency: "VND",
            ActualStatus: "PAID",
            EntityClaims: [],
            IssuedAt: now,
            ExpiresAt: expiresAt);

        var response = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", now, keyId, "sig", "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            new VerificationReceipt(body, "tampered-signature"));
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        using var httpClient = new HttpClient(new MockHttpHandler(httpResponse)) { BaseAddress = new Uri("http://localhost:8081") };
        var client = new IntegrityHostClient(httpClient, options, NullLogger<IntegrityHostClient>.Instance);

        var result = await client.VerifyAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("IntegrityHost.InvalidReceiptSignature", result.Error.Code);
    }

    [Fact]
    public async Task IntegrityHostClient_LifetimeExceeded_ReturnsConflict()
    {
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string pubKey = EcdsaAttestation.ExportPublicKeyBase64(signingKey);
        string keyId = "test-k1";

        var options = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
        {
            ActiveKeyId = keyId,
            PublicKeys = new Dictionary<string, string> { [keyId] = pubKey }
        });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.AddSeconds(10); // Exceeds 5s TTL

        var req = new TransactionVerificationRequest(PaymentRef: "PAY-1", Purpose: VerificationPurposes.PaymentConfirmation, CallerNonce: "nonce-1");
        string requestHash = CanonicalJournalHasher.ComputeRequestHash(req.PaymentRef, req.Purpose, req.CallerNonce);

        var body = new VerificationReceiptBody(
            Schema: 2,
            Issuer: "dprental-integrity-host",
            Audience: "payment-api",
            Environment: "production",
            ReceiptId: Guid.NewGuid(),
            KeyId: keyId,
            RequestHash: requestHash,
            CallerIdentity: "",
            CallerNonce: req.CallerNonce,
            Purpose: req.Purpose,
            IntegrityStatus: IntegrityStatuses.Valid,
            IsUsable: true,
            Reason: "OK",
            ActualAmount: 500_000m,
            ActualCurrency: "VND",
            ActualStatus: "PAID",
            EntityClaims: [],
            IssuedAt: now,
            ExpiresAt: expiresAt);

        string claimsDigest = EcdsaAttestation.ComputeClaimsDigest([]);
        string receiptMsg = EcdsaAttestation.BuildVerificationReceiptMessageV2(
            body.Schema, body.Issuer, body.Audience, body.Environment, body.ReceiptId, body.KeyId,
            body.RequestHash, body.CallerIdentity, body.CallerNonce, body.Purpose, body.IntegrityStatus,
            body.IsUsable, body.Reason, body.ActualAmount, body.ActualCurrency, body.ActualStatus,
            claimsDigest, body.IssuedAt, body.ExpiresAt);
        string sig = EcdsaAttestation.Sign(signingKey, receiptMsg);

        var response = new TransactionVerificationResponse(
            new TransactionIntegrityResult("VALID", 1, "SYSTEM", now, keyId, sig, "proof", req.CallerNonce),
            new TransactionUsabilityResult(true, "OK", 500_000m, "VND", "PAID"),
            new VerificationReceipt(body, sig));
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        using var httpClient = new HttpClient(new MockHttpHandler(httpResponse)) { BaseAddress = new Uri("http://localhost:8081") };
        var client = new IntegrityHostClient(httpClient, options, NullLogger<IntegrityHostClient>.Instance);

        var result = await client.VerifyAsync(req);

        Assert.True(result.IsFailure);
        Assert.Equal("IntegrityHost.LifetimeExceeded", result.Error.Code);
    }

    [Fact]
    public void JournalEntryFactory_NullEncryptor_MustThrowArgumentNullException_NoDefaultFallback()
    {
        // Blocker 4: Must NOT fallback to new PaymentDataEncryptor(new byte[32])
        Assert.Throws<ArgumentNullException>(() =>
            JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: "stream-1",
                streamType: "Invoice",
                version: 1,
                eventId: Guid.NewGuid(),
                eventType: "INVOICE_CREATED",
                amount: 500_000m,
                currency: "VND",
                status: "PENDING",
                confirmationSource: "SYSTEM",
                actor: "SYSTEM",
                timestampUtc: DateTimeOffset.UtcNow,
                previousHash: CanonicalJournalHasher.GenesisHash,
                encryptor: null!));
    }
}
