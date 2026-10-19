using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.DataProtection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Clients;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PaymentVerificationsApiTests : IAsyncLifetime
{
    private const string Issuer = "dprental-authservice-dev";
    private const string ServiceAudience = "payment-api";
    private const string ClientAudience = "dprental-clients";
    private const string KeyId = "payment-test-key";
    private const string VerifyScope = "payment.transaction.verify";
    private const string HistoryScope = "payment.transaction.history";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ECDsa _signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
    private readonly string _ecdsaKeyId = "dpr-ecdsa-2026-v1";
    private string _publicKeyBase64 = string.Empty;

    private string _connString = string.Empty;
    private string _dbName = string.Empty;
    private string _integrityConn = string.Empty;
    private string _integrityDbName = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    private static readonly IPaymentDataEncryptor TestEncryptor =
        new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32), "test-enc-key");

    public async Task InitializeAsync()
    {
        _publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(_signingKey);
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

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", _connString);
                builder.UseSetting("Payment:Merchant:Bank", "MBBank");
                builder.UseSetting("Payment:Merchant:Account", "0383799632");
                builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
                builder.UseSetting("Authentication:Jwt:ServiceAudience", ServiceAudience);
                builder.UseSetting("Authentication:Jwt:ClientAudience", ClientAudience);
                builder.UseSetting("Authentication:Jwt:JwksUrl", "http://localhost/mock-jwks");
                builder.UseSetting("IntegrityVerification:ActiveKeyId", _ecdsaKeyId);
                builder.UseSetting($"IntegrityVerification:PublicKeys:{_ecdsaKeyId}", _publicKeyBase64);

                builder.ConfigureTestServices(services =>
                {
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(
                        Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, opt =>
                    {
                        opt.TokenValidationParameters.ValidIssuer = Issuer;
                        opt.TokenValidationParameters.ValidAudiences = [ServiceAudience, ClientAudience];
                        opt.ConfigurationManager = new TestJwksManager(_rsa, KeyId);
                    });

                    var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
                    {
                        SigningKeyId = _ecdsaKeyId,
                        PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(_signingKey)
                    }));

                    var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
                    {
                        ActiveKeyId = _ecdsaKeyId,
                        PublicKeys = new Dictionary<string, string> { [_ecdsaKeyId] = _publicKeyBase64 }
                    });

                    services.AddHttpClient<ITransactionIntegrityChecker, IntegrityHostClient>()
                        .ConfigurePrimaryHttpMessageHandler(() =>
                        {
                            var iCtx = new IntegrityDbContext(new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options);
                            var pCtx = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connString));
                            var engine = new IntegrityVerificationEngine(iCtx, pCtx, keyManager, TestEncryptor, verifyOptions);
                            return new MockIntegrityHandler(engine);
                        });
                });
            });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        _rsa.Dispose();
        _signingKey.Dispose();
        if (!string.IsNullOrEmpty(_dbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_dbName);
        }
        if (!string.IsNullOrEmpty(_integrityDbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityDbName);
        }
    }

    private string CreateToken(string[]? scopes = null, bool isService = true, bool isAdmin = false)
    {
        var claims = new List<Claim>();
        if (isService)
        {
            claims.Add(new Claim("u", "svc"));
            claims.Add(new Claim("sub", "rental-service"));
        }
        else
        {
            claims.Add(new Claim("sub", Guid.NewGuid().ToString("D")));
            if (isAdmin)
            {
                claims.Add(new Claim("r", "adm"));
            }
        }

        if (scopes != null)
        {
            claims.Add(new Claim("s", string.Join(" ", scopes)));
        }

        var rsaKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        var creds = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: isService ? ServiceAudience : ClientAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Verify_WithoutToken_Returns401Unauthorized()
    {
        using var client = _factory.CreateClient();
        var req = new TransactionVerificationRequest("INV-123", "SETTLEMENT", "nonce-1");
        using var response = await client.PostAsJsonAsync("/api/v1/payment/verifications", req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_MissingScope_Returns403Forbidden()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken(["payment.invoice.write"])); // Wrong scope

        var req = new TransactionVerificationRequest("INV-123", "SETTLEMENT", "nonce-1");
        using var response = await client.PostAsJsonAsync("/api/v1/payment/verifications", req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Verify_UnconfirmedTransaction_Returns202Accepted()
    {
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        string invoiceRef;
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            var repo = new EfInvoiceRepository(ctx);
            var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor);
            var creation = new InvoiceCreationService(repo, Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions { Bank = "MBBank", Account = "0383799632" }), lifecycle, TestEncryptor);

            var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-VERIFY-202", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-verify-202"));
            invoiceRef = createRes.Value.InvoiceRef;
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken([VerifyScope]));

        var req = new TransactionVerificationRequest(invoiceRef, "SETTLEMENT", "nonce-202");
        using var response = await client.PostAsJsonAsync("/api/v1/payment/verifications", req);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionVerificationResponse>();
        Assert.NotNull(body);
        Assert.Equal(IntegrityStatuses.PendingConfirmation, body.Integrity.Status);
        Assert.False(body.Usability.IsUsable);
    }

    [Fact]
    public async Task Verify_ConfirmedValidTransaction_Returns200OK()
    {
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        string invoiceRef;
        Guid orderId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (var ctx = new PaymentServiceDbContext(options))
        {
            var repo = new EfInvoiceRepository(ctx);
            var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor);
            var creation = new InvoiceCreationService(repo, Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions { Bank = "MBBank", Account = "0383799632" }), lifecycle, TestEncryptor);

            var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                orderId, "ORD-VERIFY-200", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 0m, 0m)],
                "op-verify-200"));
            invoiceRef = createRes.Value.InvoiceRef;

            // Confirm entry with valid signature
            var entry = await ctx.TransactionJournals.SingleAsync(j => j.StreamId == invoiceRef);
            string message = EcdsaAttestation.BuildAttestationMessage(_ecdsaKeyId, entry.StreamId, entry.Version, entry.EventId, entry.EntryHash, now);
            string sig = EcdsaAttestation.Sign(_signingKey, message);
            entry.Confirm(sig, _ecdsaKeyId, now);
            await ctx.SaveChangesAsync();

            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                var att = AttestationRecord.Create(
                    entry.StreamId, entry.Version, entry.EventId, entry.EntryHash, entry.PayloadHash, entry.PreviousHash, sig, _ecdsaKeyId, now);
                iCtx.Attestations.Add(att);
                var manifest = StreamManifest.Create(entry.StreamId, entry.Version, entry.EntryHash, entry.EventId, now);
                iCtx.Manifests.Add(manifest);
                await iCtx.SaveChangesAsync();
            }
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken([VerifyScope]));

        var req = new TransactionVerificationRequest(
            PaymentRef: invoiceRef,
            Purpose: VerificationPurposes.PaymentConfirmation,
            CallerNonce: "nonce-200",
            ExpectedAmount: 0m,
            ExpectedCurrency: "VND",
            ExpectedOrderId: orderId);

        using var response = await client.PostAsJsonAsync("/api/v1/payment/verifications", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionVerificationResponse>();
        Assert.NotNull(body);
        Assert.Equal(IntegrityStatuses.Valid, body.Integrity.Status);
        Assert.True(body.Usability.IsUsable);
        Assert.Equal("OK", body.Usability.Reason);
        Assert.NotNull(body.Integrity.Proof);
    }

    [Fact]
    public async Task History_CursorPagination_WorksCleanly()
    {
        var options = PostgresTestDatabaseHelper.CreateOptions(_connString);
        await using (var ctx = new PaymentServiceDbContext(options))
        {
            var repo = new EfInvoiceRepository(ctx);
            var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor);
            var creation = new InvoiceCreationService(repo, Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions { Bank = "MBBank", Account = "0383799632" }), lifecycle, TestEncryptor);

            for (int i = 0; i < 5; i++)
            {
                await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), $"ORD-HIST-{i}", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 100_000m * (i + 1), 0m)],
                    $"op-hist-{i}"));
            }
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CreateToken([HistoryScope]));

        // Page 1: limit 2
        using var page1Res = await client.GetAsync("/api/v1/payment/history?limit=2");
        Assert.Equal(HttpStatusCode.OK, page1Res.StatusCode);
        var page1 = await page1Res.Content.ReadFromJsonAsync<CursorPaginatedHistoryResponse>();
        Assert.NotNull(page1);
        Assert.Equal(2, page1.Items.Count);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);

        // Page 2: with cursor
        using var page2Res = await client.GetAsync($"/api/v1/payment/history?limit=2&cursor={Uri.EscapeDataString(page1.NextCursor)}");
        Assert.Equal(HttpStatusCode.OK, page2Res.StatusCode);
        var page2 = await page2Res.Content.ReadFromJsonAsync<CursorPaginatedHistoryResponse>();
        Assert.NotNull(page2);
        Assert.Equal(2, page2.Items.Count);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);
    }

    private sealed class TestJwksManager(RSA rsa, string keyId) : Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>
    {
        public Task<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            var config = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = keyId });
            return Task.FromResult(config);
        }

        public void RequestRefresh() { }
    }

    private sealed class MockIntegrityHandler(IntegrityVerificationEngine engine) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/internal/v1/integrity/verify", StringComparison.Ordinal) == true)
            {
                var reqDto = await request.Content!.ReadFromJsonAsync<TransactionVerificationRequest>(cancellationToken: cancellationToken);
                var result = await engine.VerifyAsync(reqDto!, cancellationToken);
                if (result.IsSuccess)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(result.Value)
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(result.Error)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
