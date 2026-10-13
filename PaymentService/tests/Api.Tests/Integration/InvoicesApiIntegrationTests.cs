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
using Dprental.PaymentService.Domain.Repository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private string _connString = string.Empty;
    private string _dbName = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        (_connString, _dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
                builder.UseSetting("Authentication:Jwt:ServiceAudience", ServiceAudience);
                builder.UseSetting("Authentication:Jwt:ClientAudience", ClientAudience);
                builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/jwks");

                builder.ConfigureTestServices(services =>
                {
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, opt =>
                    {
                        opt.TokenValidationParameters.ValidIssuer = Issuer;
                        opt.TokenValidationParameters.ValidAudiences = [ServiceAudience, ClientAudience];
                        opt.ConfigurationManager = new TestJwksManager(_rsa, KeyId);
                    });
                });
            });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        _rsa.Dispose();
        if (!string.IsNullOrEmpty(_dbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_dbName);
        }
    }

    [Fact]
    public async Task CreateInvoice_ValidServiceToken_Returns201Created_WithContractV1()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
            svcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateServiceToken());
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
            svcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateServiceToken());
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
    public async Task AuthenticationAndAuthorization_EnforcesSecurityBoundaries()
    {
        using var client = _factory.CreateClient();

        // 1. Anonymous -> 401
        using var anonResp = await client.PostAsJsonAsync("/api/v1/invoices", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Service endpoint with user token -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateUserToken(Guid.NewGuid()));
        using var userOnSvc = await client.PostAsJsonAsync("/api/v1/invoices", new { });
        Assert.Equal(HttpStatusCode.Forbidden, userOnSvc.StatusCode);

        // 3. Service token missing scope -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken(scope: "payment.refund.write"));
        using var badScope = await client.PostAsJsonAsync("/api/v1/invoices", new { });
        Assert.Equal(HttpStatusCode.Forbidden, badScope.StatusCode);

        // 4. GET endpoint with service token -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());
        using var svcOnUser = await client.GetAsync("/api/v1/invoices/INV-TEST");
        Assert.Equal(HttpStatusCode.Forbidden, svcOnUser.StatusCode);
    }

    [Fact]
    public async Task CancelInvoice_ValidFlow_Returns200AndEnforcesRules()
    {
        Guid orderId = Guid.NewGuid();
        string invoiceRef;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateServiceToken());

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
            svcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateServiceToken());
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
            svcClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateServiceToken());
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
}
