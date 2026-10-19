using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Infrastructure.Clients;
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
