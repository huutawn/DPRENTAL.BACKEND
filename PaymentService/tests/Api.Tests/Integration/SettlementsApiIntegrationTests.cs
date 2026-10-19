using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class SettlementsApiIntegrationTests : IAsyncLifetime
{
    private const string Issuer = "dprental-authservice-dev";
    private const string Audience = "payment-api";
    private const string KeyId = "settlement-test-key";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ECDsa _ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
    private const string SigningKeyId = "dpr-ecdsa-2026-v1";
    private string _connectionString = string.Empty;
    private string _databaseName = string.Empty;
    private string _integrityConn = string.Empty;
    private string _integrityDbName = string.Empty;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        (_connectionString, _databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        (_integrityConn, _integrityDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using (var db = new PaymentServiceDbContext(
            PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            await db.Database.MigrateAsync();
        }
        var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
        await using (var iCtx = new IntegrityDbContext(iOptions))
        {
            await iCtx.Database.MigrateAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("Payment:Merchant:Bank", "MBBank");
            builder.UseSetting("Payment:Merchant:Account", "0383799632");
            builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
            builder.UseSetting("Authentication:Jwt:ServiceAudience", Audience);
            builder.UseSetting("Authentication:Jwt:ClientAudience", "dprental-clients");
            builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/jwks");
            builder.UseSetting("IntegrityVerification:ActiveKeyId", SigningKeyId);
            builder.UseSetting($"IntegrityVerification:PublicKeys:{SigningKeyId}", EcdsaAttestation.ExportPublicKeyBase64(_ecdsa));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = Issuer;
                    options.TokenValidationParameters.ValidAudiences = [Audience, "dprental-clients"];
                    options.ConfigurationManager = new TestJwksManager(_rsa);
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
                        var iCtx = new IntegrityDbContext(new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options);
                        var pCtx = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
                        var engine = new IntegrityVerificationEngine(iCtx, pCtx, keyManager, new PaymentDataEncryptor(new byte[32]), verifyOptions);
                        return new MockIntegrityHandler(engine);
                    });
            });
        });
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        _ecdsa.Dispose();
        _rsa.Dispose();
        if (_databaseName.Length > 0)
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_databaseName);
        if (_integrityDbName.Length > 0)
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityDbName);
    }

    [Fact]
    public async Task Service_scope_is_required_and_valid_settlement_replays()
    {
        var now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 90_000m).Value;
        var invoice = Invoice.Create(Guid.NewGuid(), "ORD-SETTLEMENT-API", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [item], "DPR-SETTLEMENT-API", "MBBank", "0383799632", now).Value;
        Assert.True(invoice.MarkPaid("PAY-SETTLEMENT-API", invoice.TotalAmount, "VND", now).IsSuccess);

        await using (var db = new PaymentServiceDbContext(
            PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(invoice);
            var journal = JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: invoice.PaymentRef!,
                streamType: JournalStreamTypes.PaymentTransaction,
                version: 1,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.PaymentMatched,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: "PAID",
                confirmationSource: JournalConfirmationSources.Webhook,
                actor: "WEBHOOK",
                timestampUtc: now,
                previousHash: CanonicalJournalHasher.GenesisHash,
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: new PaymentDataEncryptor(new byte[32]));
            string attMsg = EcdsaAttestation.BuildAttestationMessage(SigningKeyId, journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, now);
            string attSig = EcdsaAttestation.Sign(_ecdsa, attMsg);
            journal.Confirm(attSig, SigningKeyId, now);
            db.TransactionJournals.Add(journal);
            await db.SaveChangesAsync();
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                var att = AttestationRecord.Create(
                    journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, journal.PayloadHash, journal.PreviousHash, attSig, SigningKeyId, now);
                iCtx.Attestations.Add(att);
                var manifest = StreamManifest.Create(journal.StreamId, journal.Version, journal.EntryHash, journal.EventId, now);
                iCtx.Manifests.Add(manifest);
                await iCtx.SaveChangesAsync();
            }
        }

        var request = new SettleRentalRequest(invoice.RentalOrderId, item.RentalItemId,
            invoice.InvoiceRef, invoice.PaymentRef!, item.SupplierId,
            item.DepositAmount, 20_000m, 70_000m, "VND", "DEC-API", "settle-api-001");

        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/settlements", request)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("payment.invoice.write"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/settlements", request)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("payment.settlement.write"));
        var created = await client.PostAsJsonAsync("/api/v1/settlements", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<SettleRentalResponse>();
        Assert.NotNull(body);
        Assert.Equal(SettlementStatus.PendingManualRefund, body.Status);
        Assert.False(body.IsReplay);

        var replay = await client.PostAsJsonAsync("/api/v1/settlements", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<SettleRentalResponse>();
        Assert.NotNull(replayBody);
        Assert.True(replayBody.IsReplay);
        Assert.Equal(body.SettlementRef, replayBody.SettlementRef);
    }

    [Fact]
    public async Task Admin_can_confirm_manual_refund_but_service_and_customer_cannot()
    {
        var now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 90_000m).Value;
        var invoice = Invoice.Create(Guid.NewGuid(), "ORD-REFUND-API", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [item], "DPR-REFUND-API", "MBBank", "0383799632", now).Value;
        Assert.True(invoice.MarkPaid("PAY-REFUND-API", invoice.TotalAmount, "VND", now).IsSuccess);
        Guid requirementId;
        await using (var db = new PaymentServiceDbContext(
            PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(invoice);
            var journal = JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: invoice.PaymentRef!,
                streamType: JournalStreamTypes.PaymentTransaction,
                version: 1,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.PaymentMatched,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: "PAID",
                confirmationSource: JournalConfirmationSources.Webhook,
                actor: "WEBHOOK",
                timestampUtc: now,
                previousHash: CanonicalJournalHasher.GenesisHash,
                paymentRef: invoice.PaymentRef,
                invoiceRef: invoice.InvoiceRef,
                rentalOrderId: invoice.RentalOrderId,
                encryptor: new PaymentDataEncryptor(new byte[32]));
            string attMsg2 = EcdsaAttestation.BuildAttestationMessage(SigningKeyId, journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, now);
            string attSig2 = EcdsaAttestation.Sign(_ecdsa, attMsg2);
            journal.Confirm(attSig2, SigningKeyId, now);
            db.TransactionJournals.Add(journal);
            await db.SaveChangesAsync();
            var settlement = RentalItemSettlement.Create(item, 20_000m, "DEC-REFUND-API", "settle-refund-api").Value;
            var result = await new Dprental.PaymentService.Infrastructure.Repositories.EfSettlementLedger(db, TimeProvider.System, new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32)))
                .RecordSettlementAsync(settlement, CancellationToken.None);
            Assert.True(result.IsSuccess);
            requirementId = await db.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
            var iOptions2 = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
            await using (var iCtx = new IntegrityDbContext(iOptions2))
            {
                var att = AttestationRecord.Create(
                    journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, journal.PayloadHash, journal.PreviousHash, attSig2, SigningKeyId, now);
                iCtx.Attestations.Add(att);
                var manifest = StreamManifest.Create(journal.StreamId, journal.Version, journal.EntryHash, journal.EventId, now);
                iCtx.Manifests.Add(manifest);
                await iCtx.SaveChangesAsync();
            }
        }

        using var client = _factory.CreateClient();
        var url = $"/api/v1/manual-refunds/{requirementId}/complete";
        var request = new CompleteManualRefundRequest("BANK-ADMIN-001");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("payment.settlement.write"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm"));
        var response = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CompleteManualRefundResponse>();
        Assert.NotNull(body);
        Assert.False(body.IsReplay);
        Assert.Equal("BANK-ADMIN-001", body.ExternalTransferReference);
        Assert.NotEqual(Guid.Empty, body.CompletedBy);

        var replay = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<CompleteManualRefundResponse>())!.IsReplay);
    }

    private string Token(string scope)
    {
        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString("D")),
            new Claim("u", "svc"),
            new Claim("s", scope)
        };
        var jwt = new JwtSecurityToken(Issuer, Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private string UserToken(string role)
    {
        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString("D")),
            new Claim("r", role)
        };
        var jwt = new JwtSecurityToken(Issuer, "dprental-clients", claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class TestJwksManager(RSA rsa) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = KeyId });
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

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
