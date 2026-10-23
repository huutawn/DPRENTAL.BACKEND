using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Application.Clients;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.IntegrityHost.Services;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class InvoicesApiIntegrationTests : IAsyncLifetime
{
    private const string Issuer = "dprental-authservice-dev";
    private const string ServiceAudience = "payment-api";
    private const string ClientAudience = "dprental-clients";
    private const string KeyId = "payment-test-key";
    private const string WriteScope = "payment.invoice.write";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ECDsa _ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
    private const string SigningKeyId = "dpr-ecdsa-2026-v1";
    private string _connString = string.Empty;
    private string _dbName = string.Empty;
    private string _integrityConn = string.Empty;
    private string _integrityDbName = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;
    public async Task InitializeAsync()
    {
        (_connString, _dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        (_integrityConn, _integrityDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }
        var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
        await using (var iCtx = new IntegrityDbContext(iOptions))
        {
            await iCtx.Database.MigrateAsync();
        }
        _factory = new PaymentWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
                builder.UseSetting("Authentication:Jwt:ServiceAudience", ServiceAudience);
                builder.UseSetting("Authentication:Jwt:ClientAudience", ClientAudience);
                builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/jwks");
                builder.UseSetting("InternalAuthentication:RentalApiKey", PaymentWebApplicationFactory.DefaultInternalApiKey);
                builder.UseSetting("IntegrityVerification:ActiveKeyId", SigningKeyId);
                builder.UseSetting($"IntegrityVerification:PublicKeys:{SigningKeyId}", EcdsaAttestation.ExportPublicKeyBase64(_ecdsa));
                builder.ConfigureLogging(logging => logging.ClearProviders());

                builder.ConfigureTestServices(services =>
                {
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opt =>
                    {
                        opt.TokenValidationParameters.ValidIssuer = Issuer;
                        opt.TokenValidationParameters.ValidAudiences = [ServiceAudience, ClientAudience];
                        opt.ConfigurationManager = new TestJwksManager(_rsa, KeyId);
                    });

                    var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
                    {
                        SigningKeyId = SigningKeyId,
                        PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(_ecdsa)
                    }));

                    var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
                    {
                        ActiveKeyId = SigningKeyId,
                        PublicKeys = new Dictionary<string, string> { [SigningKeyId] = EcdsaAttestation.ExportPublicKeyBase64(_ecdsa) }
                    });

                    services.AddHttpClient<ITransactionIntegrityChecker, IntegrityHostClient>()
                        .ConfigurePrimaryHttpMessageHandler(() =>
                        {
                            return new MockIntegrityHandler(() =>
                            {
                                var iCtx = new IntegrityDbContext(new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options);
                                var pCtx = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connString));
                                return new IntegrityVerificationEngine(iCtx, pCtx, keyManager, new PaymentDataEncryptor(new byte[32]), verifyOptions);
                            });
                        });
                });
            });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        _ecdsa.Dispose();
        _rsa.Dispose();
        if (!string.IsNullOrEmpty(_dbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_dbName);
        }
        if (!string.IsNullOrEmpty(_integrityDbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityDbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ValidServiceToken_Returns201Created_WithContractV1()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        var req = new CreateInvoiceRequest(
            orderId, "ORD-HTTP-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 1_000_000m)],
            "op-http-create-1");

        using var response = await client.PostAsJsonAsync("/api/v1/invoices", req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        Assert.NotNull(body);
        Assert.StartsWith("INV-", body.InvoiceRef, StringComparison.Ordinal);
        Assert.Equal(1_500_000m, body.TotalAmount);
        Assert.Equal("VND", body.Currency);
        Assert.False(body.IsReplay);
    }

    [Fact]
    public async Task CreateInvoice_WithPlus07OffsetDeadline_HttpRoundtripAndUtcReplay_Returns201Then200()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();

        var deadlinePlus7 = new DateTimeOffset(DateTime.UtcNow.AddMinutes(30)).ToOffset(TimeSpan.FromHours(7));
        var reqPlus7 = new CreateInvoiceRequest(
            orderId, "ORD-HTTP-TZ-001", renterId, "VND", deadlinePlus7,
            [new CreateInvoiceItemRequest(itemId, supplierId, 500_000m, 1_000_000m)],
            "op-http-tz-001");

        using var createResp = await client.PostAsJsonAsync("/api/v1/invoices", reqPlus7);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        Assert.NotNull(createBody);
        Assert.False(createBody.IsReplay);
        Assert.Equal(deadlinePlus7.ToUniversalTime(), createBody.PaymentDeadline);

        // Replay with equivalent UTC deadline
        var reqUtc = new CreateInvoiceRequest(
            orderId, "ORD-HTTP-TZ-001", renterId, "VND", deadlinePlus7.ToUniversalTime(),
            [new CreateInvoiceItemRequest(itemId, supplierId, 500_000m, 1_000_000m)],
            "op-http-tz-001");

        using var replayResp = await client.PostAsJsonAsync("/api/v1/invoices", reqUtc);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);

        var replayBody = await replayResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        Assert.NotNull(replayBody);
        Assert.True(replayBody.IsReplay);
        Assert.Equal(createBody.InvoiceRef, replayBody.InvoiceRef);
    }

    [Fact]
    public async Task CreateInvoice_Replay_Returns200Ok_WithIsReplayTrue()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        Guid orderId = Guid.NewGuid();
        var req = new CreateInvoiceRequest(
            orderId, "ORD-REPLAY-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 300_000m, 200_000m)],
            "op-http-replay-1");

        using var first = await client.PostAsJsonAsync("/api/v1/invoices", req);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var replay = await client.PostAsJsonAsync("/api/v1/invoices", req);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var body = await replay.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        Assert.NotNull(body);
        Assert.True(body.IsReplay);
        Assert.Equal(500_000m, body.TotalAmount);
    }

    [Fact]
    public async Task CreateInvoice_CallerSendingExtraTotalAmount_DoesNotAffectCalculation()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid supplierId = Guid.NewGuid();
        string deadline = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O");

        // Send raw JSON containing a spoofed "totalAmount" field
        string rawJson = $$"""
        {
          "rentalOrderId": "{{orderId}}",
          "orderCode": "ORD-SPOOF-001",
          "renterId": "{{renterId}}",
          "currency": "VND",
          "paymentDeadline": "{{deadline}}",
          "totalAmount": 999999999,
          "items": [
            {
              "rentalItemId": "{{itemId}}",
              "supplierId": "{{supplierId}}",
              "rentalAmount": 400000,
              "depositAmount": 600000
            }
          ],
          "operationKey": "op-spoof-001"
        }
        """;

        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/invoices", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        Assert.NotNull(body);
        // Correctly calculates 400,000 + 600,000 = 1,000,000; completely ignores 999,999,999
        Assert.Equal(1_000_000m, body.TotalAmount);
    }

    [Fact]
    public async Task GetInvoice_ValidRenterToken_Returns200WithCorrectQrUrlAndMerchantSnapshot()
    {
        Guid renterId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        string invoiceRef;

        // 1. Create invoice via service token
        using (var svcClient = _factory.CreateClient())
        {
            svcClient.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
            var createReq = new CreateInvoiceRequest(
                orderId, "ORD-GET-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 250_000m, 500_000m)],
                "op-get-001");

            var createResp = await svcClient.PostAsJsonAsync("/api/v1/invoices", createReq);
            Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
            var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
            invoiceRef = createBody!.InvoiceRef;
        }

        // 2. Read invoice via renter token
        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateUserToken(renterId));

        using var getResp = await userClient.GetAsync($"/api/v1/invoices/{invoiceRef}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var getBody = await getResp.Content.ReadFromJsonAsync<GetInvoiceResponse>();
        Assert.NotNull(getBody);
        Assert.Equal(invoiceRef, getBody.InvoiceRef);
        Assert.Equal(750_000m, getBody.TotalAmount);
        Assert.Equal("VND", getBody.Currency);
        Assert.Equal("MBBank", getBody.Bank);
        Assert.Equal("0383799632", getBody.Account);
        Assert.Equal("PENDING", getBody.Status);
        Assert.NotNull(getBody.TransferContent);
        Assert.StartsWith("DPR ", getBody.TransferContent, StringComparison.Ordinal);

        // QR verification
        Assert.NotNull(getBody.QrUrl);
        Assert.StartsWith("https://vietqr.app/img?", getBody.QrUrl, StringComparison.Ordinal);
        Assert.Contains("acc=0383799632", getBody.QrUrl, StringComparison.Ordinal);
        Assert.Contains("bank=MBBank", getBody.QrUrl, StringComparison.Ordinal);
        Assert.Contains("amount=750000", getBody.QrUrl, StringComparison.Ordinal);
        Assert.Contains("template=compact", getBody.QrUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", getBody.QrUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetInvoice_DifferentRenter_Returns404NotFound()
    {
        Guid actualRenterId = Guid.NewGuid();
        Guid otherRenterId = Guid.NewGuid();
        string invoiceRef;

        using (var svcClient = _factory.CreateClient())
        {
            svcClient.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
            var createResp = await svcClient.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-404-001", actualRenterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 0m)],
                "op-diff-renter-1"));
            var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
            invoiceRef = createBody!.InvoiceRef;
        }

        // Call GET with other renter ID
        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateUserToken(otherRenterId));

        using var getResp = await userClient.GetAsync($"/api/v1/invoices/{invoiceRef}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        var problem = await getResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVOICE_NOT_FOUND", problem.Title);
    }

    [Fact]
    public async Task GetInvoiceStatus_AccessTokenCookie_Returns200()
    {
        Guid renterId = Guid.NewGuid();
        string invoiceRef;

        using (var serviceClient = _factory.CreateClient())
        {
            serviceClient.DefaultRequestHeaders.Add(
                "X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
            var createResponse = await serviceClient.PostAsJsonAsync(
                "/api/v1/invoices",
                new CreateInvoiceRequest(
                    Guid.NewGuid(),
                    "ORD-STATUS-COOKIE-001",
                    renterId,
                    "VND",
                    DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 10_000m, 0m)],
                    "op-status-cookie-001"));
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            invoiceRef = (await createResponse.Content.ReadFromJsonAsync<CreateInvoiceResponse>())!.InvoiceRef;
        }

        using var renterClient = _factory.CreateClient();
        renterClient.DefaultRequestHeaders.Add(
            "Cookie", $"accessToken={CreateUserToken(renterId)}");

        using var statusResponse = await renterClient.GetAsync(
            $"/api/v1/invoices/{invoiceRef}/status");

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        using var otherRenterClient = _factory.CreateClient();
        otherRenterClient.DefaultRequestHeaders.Add(
            "Cookie", $"accessToken={CreateUserToken(Guid.NewGuid())}");
        using var otherRenterResponse = await otherRenterClient.GetAsync(
            $"/api/v1/invoices/{invoiceRef}/status");
        Assert.Equal(HttpStatusCode.NotFound, otherRenterResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticationAndAuthorization_EnforcesSecurityBoundaries()
    {
        using var client = _factory.CreateClient();

        // 1. Missing API key -> 401
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        using var anonResp = await client.PostAsJsonAsync("/api/v1/invoices", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // A service JWT alone does not replace the dedicated Rental API key.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateServiceToken());
        using var jwtOnlyResp = await client.PostAsJsonAsync("/api/v1/invoices", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, jwtOnlyResp.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        // 2. Key present, invalid JWT on user endpoint -> 401
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var badJwtResp = await client.GetAsync("/api/v1/invoices/INV-TEST");
        Assert.Equal(HttpStatusCode.Unauthorized, badJwtResp.StatusCode);

        // 3. GET endpoint without user JWT -> 401
        client.DefaultRequestHeaders.Authorization = null;
        using var noUserJwtResp = await client.GetAsync("/api/v1/invoices/INV-TEST");
        Assert.Equal(HttpStatusCode.Unauthorized, noUserJwtResp.StatusCode);

        client.DefaultRequestHeaders.Add("Cookie", "accessToken=invalid-token");
        using var invalidCookieResp = await client.GetAsync("/api/v1/invoices/INV-TEST/status");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidCookieResp.StatusCode);
    }

    [Fact]
    public async Task CancelInvoice_ValidFlow_Returns200AndEnforcesRules()
    {
        Guid orderId = Guid.NewGuid();
        string invoiceRef;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        // 1. Create invoice
        var createResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
            orderId, "ORD-CANCEL-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 200_000m, 0m)],
            "op-create-for-cancel"));
        var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        invoiceRef = createBody!.InvoiceRef;
        // 2. Mismatched route vs body -> 400
        var mismatchReq = new CancelInvoiceRequest(orderId, "INV-DIFFERENT", "Reason", "op-cancel-1");
        using var mismatchResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/cancel", mismatchReq);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchResp.StatusCode);

        // 3. Successful cancel -> 200
        var validReq = new CancelInvoiceRequest(orderId, invoiceRef, "Renter requested", "op-cancel-valid");
        using var cancelResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/cancel", validReq);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var cancelBody = await cancelResp.Content.ReadFromJsonAsync<CancelInvoiceResponse>();
        Assert.NotNull(cancelBody);
        Assert.True(cancelBody.Cancelled);
        Assert.False(cancelBody.IsReplay);

        // 4. Replay cancel -> 200 with IsReplay true
        using var replayResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/cancel", validReq);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
        var replayBody = await replayResp.Content.ReadFromJsonAsync<CancelInvoiceResponse>();
        Assert.NotNull(replayBody);
        Assert.True(replayBody.IsReplay);
    }
    [Fact]
    public async Task CancelInvoice_PaidInvoice_Returns409Conflict_InvoiceAlreadyPaid()
    {
        Guid orderId = Guid.NewGuid();
        string invoiceRef;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        // Create zero-total invoice (initializes directly to PAID)
        var createResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
            orderId, "ORD-PAID-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 0m, 0m)],
            "op-create-paid-cancel"));
        var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
        invoiceRef = createBody!.InvoiceRef;

        // Attempt to cancel paid invoice
        var cancelReq = new CancelInvoiceRequest(orderId, invoiceRef, "Try to cancel", "op-cancel-paid-attempt");
        using var cancelResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/cancel", cancelReq);
        Assert.Equal(HttpStatusCode.Conflict, cancelResp.StatusCode);

        var problem = await cancelResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVOICE_ALREADY_PAID", problem.Title);
    }

    [Fact]
    public async Task GetInvoice_PaidInvoice_HasNullQrUrl()
    {
        Guid renterId = Guid.NewGuid();
        string invoiceRef;

        using (var svcClient = _factory.CreateClient())
        {
            svcClient.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
            var createResp = await svcClient.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-PAID-QR-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 0m, 0m)],
                "op-paid-qr-1"));
            var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
            invoiceRef = createBody!.InvoiceRef;
        }

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateUserToken(renterId));

        using var getResp = await userClient.GetAsync($"/api/v1/invoices/{invoiceRef}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var getBody = await getResp.Content.ReadFromJsonAsync<GetInvoiceResponse>();
        Assert.NotNull(getBody);
        Assert.Equal("PAID", getBody.Status);
        Assert.Null(getBody.QrUrl);
    }

    [Fact]
    public async Task GetInvoice_QrUrlIsStable_WhenMerchantConfigurationChangesAfterInvoiceCreated()
    {
        Guid renterId = Guid.NewGuid();
        string invoiceRef;

        using (var svcClient = _factory.CreateClient())
        {
            svcClient.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
            var createResp = await svcClient.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-SNAPSHOT-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-snapshot-test"));
            var createBody = await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>();
            invoiceRef = createBody!.InvoiceRef;
        }

        // Change merchant options in container
        var merchantOptions = _factory.Services.GetRequiredService<IOptions<PaymentMerchantOptions>>().Value;
        string originalBank = merchantOptions.Bank;
        string originalAccount = merchantOptions.Account;
        try
        {
            merchantOptions.Bank = "Techcombank";
            merchantOptions.Account = "999988887777";

            // GET invoice should still return snapshotted original bank & account
            using var userClient = _factory.CreateClient();
            userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateUserToken(renterId));

            using var getResp = await userClient.GetAsync($"/api/v1/invoices/{invoiceRef}");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

            var getBody = await getResp.Content.ReadFromJsonAsync<GetInvoiceResponse>();
            Assert.NotNull(getBody);
            Assert.Equal(originalBank, getBody.Bank);
            Assert.Equal(originalAccount, getBody.Account);
            Assert.NotNull(getBody.QrUrl);
            Assert.Contains($"bank={originalBank}", getBody.QrUrl, StringComparison.Ordinal);
            Assert.Contains($"acc={originalAccount}", getBody.QrUrl, StringComparison.Ordinal);
            Assert.DoesNotContain("Techcombank", getBody.QrUrl, StringComparison.Ordinal);
        }
        finally
        {
            merchantOptions.Bank = originalBank;
            merchantOptions.Account = originalAccount;
        }
    }

    [Fact]
    public async Task AcceptLatePayment_ValidLateInvoice_SucceedsAndReplays()
    {
        await using var h = FinancialWorkflowHarness.Attach(_connString, _integrityConn, _ecdsa);
        var invoice = await h.SeedAsync(paid: true, count: 1, late: true);
        string invoiceRef = invoice.InvoiceRef;
        string paymentRef = invoice.PaymentRef!;
        Guid orderId = invoice.RentalOrderId;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        // 1. Accept late payment
        var acceptReq = new AcceptLatePaymentRequest(
            "op-accept-late-1",
            orderId,
            paymentRef,
            "rec-order-1");

        var acceptResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/accept-late-payment", acceptReq);
        Assert.Equal(HttpStatusCode.OK, acceptResp.StatusCode);
        var acceptBody = await acceptResp.Content.ReadFromJsonAsync<AcceptLatePaymentResponse>();
        Assert.NotNull(acceptBody);
        Assert.Equal(invoiceRef, acceptBody.InvoiceRef);
        Assert.Equal(InvoiceStatus.Paid, acceptBody.Status);
        Assert.False(acceptBody.IsReplay);
        Assert.Equal(orderId, acceptBody.RentalOrderId);
        Assert.Equal(paymentRef, acceptBody.PaymentRef);
        Assert.Equal("rec-order-1", acceptBody.RecoveryId);

        // 2. Replay with same operation key
        var replayResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/accept-late-payment", acceptReq);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
        var replayBody = await replayResp.Content.ReadFromJsonAsync<AcceptLatePaymentResponse>();
        Assert.NotNull(replayBody);
        Assert.True(replayBody.IsReplay);
        Assert.Equal(invoiceRef, replayBody.InvoiceRef);

        // 3. Conflict when replaying with same key and different payload
        var conflictReq = acceptReq with { RecoveryId = "rec-order-different" };
        var conflictResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/accept-late-payment", conflictReq);
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);

        // 4. Verify in DB: invoice status is Paid
        var dbOptions = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(dbOptions))
        {
            var inv = await ctx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Paid, inv.Status);

            var journal = await ctx.TransactionJournals
                .Where(j => j.StreamId == invoiceRef && j.EventType == JournalEventTypes.PaymentMatched)
                .OrderByDescending(j => j.Version)
                .FirstOrDefaultAsync();
            Assert.NotNull(journal);
            Assert.Equal(InvoiceStatus.Paid, journal.Status);
        }
    }

    [Fact]
    public async Task AcceptLatePayment_CustomerCancelled_ReturnsConflict()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        string invoiceRef;
        string paymentRef = "PAY-CUST-" + Guid.NewGuid().ToString("N")[..8];

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        var createResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
            orderId, "ORD-CUST-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(20),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 100_000m)],
            "op-cust-create"));
        invoiceRef = (await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>())!.InvoiceRef;

        // Cancel invoice with CustomerRequested origin
        var cancelResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/cancel", new CancelInvoiceRequest(
            orderId, invoiceRef, "Customer decided not to rent", "op-cust-cancel", InvoiceCancellationOrigins.CustomerRequested));
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        // Simulate payment ref attached
        var dbOptions = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(dbOptions))
        {
            var inv = await ctx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
            inv.RecordLatePayment(paymentRef, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        // Attempt accept late payment -> must fail
        var acceptReq = new AcceptLatePaymentRequest("op-cust-accept", orderId, paymentRef, "rec-cust-1");
        var acceptResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/accept-late-payment", acceptReq);
        Assert.Equal(HttpStatusCode.Conflict, acceptResp.StatusCode);
    }

    [Fact]
    public async Task AcceptLatePayment_Past15MinuteWindow_ReturnsConflict()
    {
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        string invoiceRef;
        string paymentRef = "PAY-EXPW-" + Guid.NewGuid().ToString("N")[..8];

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);

        // 1. Create invoice with valid deadline
        var createResp = await client.PostAsJsonAsync("/api/v1/invoices", new CreateInvoiceRequest(
            orderId, "ORD-EXPW-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 100_000m)],
            "op-expw-create"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        invoiceRef = (await createResp.Content.ReadFromJsonAsync<CreateInvoiceResponse>())!.InvoiceRef;

        // Update deadline to 20 minutes ago (past 15-minute window)
        DateTimeOffset pastDeadline = DateTimeOffset.UtcNow.AddMinutes(-20);
        var dbOptions = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(dbOptions))
        {
            var inv = await ctx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
            typeof(Invoice).GetProperty(nameof(Invoice.PaymentDeadline))!.SetValue(inv, pastDeadline);
            inv.RecordLatePayment(paymentRef, pastDeadline.AddMinutes(2), DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        var acceptReq = new AcceptLatePaymentRequest("op-expw-accept", orderId, paymentRef, "rec-expw-1");
        var acceptResp = await client.PostAsJsonAsync($"/api/v1/invoices/{invoiceRef}/accept-late-payment", acceptReq);
        Assert.Equal(HttpStatusCode.Conflict, acceptResp.StatusCode);
    }

    private string CreateServiceToken(string scope = WriteScope)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString("D")),
            new("u", "svc"),
            new("s", scope)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: ServiceAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string CreateUserToken(Guid userId)
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        var claims = new List<Claim>
        {
            new("sub", userId.ToString("D")),
            new("pc", "true")
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: ClientAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class TestJwksManager(RSA rsa, string keyId) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
            return Task.FromResult(config);
        }

        public void RequestRefresh() { }
    }

    private sealed class MockIntegrityHandler(Func<IntegrityVerificationEngine> engineFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var req = await request.Content!.ReadFromJsonAsync<TransactionVerificationRequest>(cancellationToken);
            var engine = engineFactory();
            var res = await engine.VerifyAsync(req!, cancellationToken);
            var response = new HttpResponseMessage(res.IsSuccess ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(res.IsSuccess ? res.Value : (object)res.Error)
            };
            return response;
        }
    }
    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
