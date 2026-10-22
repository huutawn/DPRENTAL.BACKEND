using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
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

        _factory = new PaymentWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
            builder.UseSetting("Payment:Merchant:Bank", "MBBank");
            builder.UseSetting("Payment:Merchant:Account", "0383799632");
            builder.UseSetting("PayoutDestination:FingerprintKey", "integration-test-payout-fingerprint-key-32-bytes");
            builder.UseSetting("Authentication:Jwt:Issuer", Issuer);
            builder.UseSetting("Authentication:Jwt:ServiceAudience", Audience);
            builder.UseSetting("Authentication:Jwt:ClientAudience", "dprental-clients");
            builder.UseSetting("Authentication:Jwt:JwksUri", "http://localhost/jwks");
            builder.UseSetting("InternalAuthentication:RentalApiKey", PaymentWebApplicationFactory.DefaultInternalApiKey);
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
                        return new MockIntegrityHandler(() =>
                        {
                            var iCtx = new IntegrityDbContext(new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options);
                            var pCtx = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
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
        if (_databaseName.Length > 0)
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_databaseName);
        if (_integrityDbName.Length > 0)
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityDbName);
    }

    [Fact]
    public async Task Service_scope_is_required_and_valid_settlement_replays()
    {
        await using var h = FinancialWorkflowHarness.Attach(_connectionString, _integrityConn, _ecdsa);
        var invoice = await h.SeedAsync();
        var item = invoice.Items[0];
        var request = new SettleRentalRequest(invoice.RentalOrderId, item.RentalItemId,
            invoice.InvoiceRef, invoice.PaymentRef!, item.SupplierId,
            item.DepositAmount, 20_000m, 70_000m, "VND", "DEC-API", "settle-api-001");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/settlements", request)).StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", PaymentWebApplicationFactory.DefaultInternalApiKey);
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
        await using var h = FinancialWorkflowHarness.Attach(_connectionString, _integrityConn, _ecdsa);
        var invoice = await h.SeedAsync();
        Guid requirementId;
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            var settlement = await h.Settlement(db).SettleAsync(FinancialWorkflowHarness.SettlementRequest(invoice), default);
            Assert.True(settlement.IsSuccess, settlement.Error.Message);
            requirementId = await db.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
        }
        await h.ConfirmAsync();
        using var client = _factory.CreateClient();
        var url = $"/api/v1/manual-refunds/{requirementId}/complete";
        var request = new CompleteManualRefundRequest(
            "BANK-ADMIN-001", "confirm-admin-001", "Đã đối chiếu chứng từ chuyển khoản.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("payment.settlement.write"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("sup"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);

        // Manual refunds GET endpoints also reject renter, supplier, and service tokens
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/manual-refunds/{requirementId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/manual-refunds/{requirementId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("payment.settlement.write"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/manual-refunds/{requirementId}")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm"));
        var response = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CompleteManualRefundResponse>();
        Assert.Equal("BANK-ADMIN-001", body!.ExternalTransferReference);
        Assert.NotEqual(Guid.Empty, body.CompletedBy);

        var replay = await client.PostAsJsonAsync(url, request);
        string replayErr = await replay.Content.ReadAsStringAsync();
        Assert.True(replay.StatusCode == HttpStatusCode.OK, $"Replay failed with {replay.StatusCode}: {replayErr}");
        var replayBody = await replay.Content.ReadFromJsonAsync<CompleteManualRefundResponse>();
        Assert.True(replayBody!.IsReplay, $"ReplayBody was: {replayErr}");
    }

    [Fact]
    public async Task Financial_reads_enforce_ownership_and_show_operational_queues()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_connectionString, _integrityConn, _ecdsa);
        var invoice = await harness.SeedAsync(count: 2);
        var now = DateTimeOffset.UtcNow;
        var renterId = invoice.RenterId;
        var item = invoice.Items[0];
        var otherItem = invoice.Items[1];
        var supplierId = item.SupplierId;
        var otherSupplierId = otherItem.SupplierId;
        var pendingItem = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 50_000m, 10_000m).Value;
        var pendingInvoice = Invoice.Create(Guid.NewGuid(), "ORD-PENDING-READ", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [pendingItem], "DPR-PENDING-READ", "MBBank", "0383799632", now).Value;
        Guid settlementId;
        Guid transactionId;
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(pendingInvoice);
            var transaction = PaymentTransaction.Create(
                "SePay", "READ-TRANSACTION-001", "PAY-READ-API", "MBBank", "0383799632", null,
                "in", 100_000m, null, now, null, null, null, null, null, null, null,
                PaymentTransactionStatus.Unmatched, PaymentTransactionReason.InvoiceNotFound, now);
            transactionId = transaction.Id;
            db.PaymentTransactions.Add(transaction);
            db.PaymentTransactions.Add(PaymentTransaction.Create(
                "SePay", "READ-TRANSACTION-002", "PAY-READ-API", "MBBank", "0383799632", null,
                "in", 100_000m, null, now, null, null, null, null, null, null, null,
                PaymentTransactionStatus.ManualRefundRequired, PaymentTransactionReason.LatePayment, now));
            db.PaymentTransactions.Add(PaymentTransaction.Create(
                "SePay", "READ-TRANSACTION-003", "PAY-READ-API", "MBBank", "0383799632", null,
                "in", 100_000m, null, now, null, null, null, null, null, null, null,
                PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AmountMismatch, now));
            await db.SaveChangesAsync();
            var firstSettlement = await harness.Settlement(db).SettleAsync(
                FinancialWorkflowHarness.SettlementRequest(invoice, 20_000m, "settle-read-api"), CancellationToken.None);
            Assert.True(firstSettlement.IsSuccess, firstSettlement.Error.Message);
            var secondRequest = new Dprental.PaymentService.Application.DTOs.Settlement.SettleRentalRequest(
                invoice.RentalOrderId, otherItem.RentalItemId, invoice.InvoiceRef, invoice.PaymentRef!, otherItem.SupplierId,
                otherItem.DepositAmount, otherItem.DepositAmount, 0m, "VND", "DEC-READ-OTHER", "settle-read-other");
            var secondSettlement = await harness.Settlement(db).SettleAsync(secondRequest, CancellationToken.None);
            Assert.True(secondSettlement.IsSuccess, secondSettlement.Error.Message);
            settlementId = await db.RentalItemSettlements.Where(x => x.RentalItemId == item.RentalItemId)
                .Select(x => x.Id).SingleAsync();
        }

        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/v1/invoices/{invoice.InvoiceRef}/status")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/invoices/{invoice.InvoiceRef}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/settlements/{settlementId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/v1/admin/payouts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/transactions/{transactionId}")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus", renterId));
        var invoiceStatus = await client.GetAsync($"/api/v1/invoices/{invoice.InvoiceRef}/status");
        Assert.Equal(HttpStatusCode.OK, invoiceStatus.StatusCode);
        var renterSettlement = await client.GetAsync($"/api/v1/settlements/{settlementId}");
        Assert.Equal(HttpStatusCode.OK, renterSettlement.StatusCode);
        using (var payload = await renterSettlement.Content.ReadFromJsonAsync<JsonDocument>())
        {
            Assert.True(payload!.RootElement.TryGetProperty("refund", out _));
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("supplier").ValueKind);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("sup", supplierId));
        var supplierSettlement = await client.GetAsync($"/api/v1/settlements/{settlementId}");
        Assert.Equal(HttpStatusCode.OK, supplierSettlement.StatusCode);
        using (var payload = await supplierSettlement.Content.ReadFromJsonAsync<JsonDocument>())
        {
            Assert.Equal(JsonValueKind.Null, payload!.RootElement.GetProperty("refund").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, payload.RootElement.GetProperty("supplier").ValueKind);
        }
        var supplierPayouts = await client.GetAsync("/api/v1/supplier/payouts");
        Assert.Equal(HttpStatusCode.OK, supplierPayouts.StatusCode);
        using (var payload = await supplierPayouts.Content.ReadFromJsonAsync<JsonDocument>())
        {
            Assert.Equal(1, payload!.RootElement.GetProperty("totalCount").GetInt64());
            Assert.Equal(item.RentalItemId, payload.RootElement.GetProperty("items")[0].GetProperty("rentalItemId").GetGuid());
            Assert.False(payload.RootElement.GetProperty("items")[0].TryGetProperty("destinationRef", out _));
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("sup", otherSupplierId));
        var otherSupplierPayouts = await client.GetAsync("/api/v1/supplier/payouts");
        Assert.Equal(HttpStatusCode.OK, otherSupplierPayouts.StatusCode);
        using (var payload = await otherSupplierPayouts.Content.ReadFromJsonAsync<JsonDocument>())
        {
            Assert.Equal(1, payload!.RootElement.GetProperty("totalCount").GetInt64());
            Assert.Equal(otherItem.RentalItemId, payload.RootElement.GetProperty("items")[0].GetProperty("rentalItemId").GetGuid());
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm"));
        var pendingRefunds = await client.GetAsync("/api/v1/admin/manual-refunds");
        Assert.Equal(HttpStatusCode.OK, pendingRefunds.StatusCode);
        var pendingPayouts = await client.GetAsync("/api/v1/admin/payouts");
        Assert.Equal(HttpStatusCode.OK, pendingPayouts.StatusCode);
        await using (var checkDb = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            var checkRepository = new Dprental.PaymentService.Domain.Repositories.EfFinancialReadRepository(checkDb);
            var directQueue = await checkRepository.ListReviewAsync(
                Dprental.PaymentService.Domain.IRepositories.FinancialReviewKind.SettlementPendingClose,
                new Dprental.PaymentService.Domain.IRepositories.DateRange(null, null),
                new Dprental.PaymentService.Domain.IRepositories.PageRequest(1, 20), CancellationToken.None);
            Assert.Equal(1, directQueue.TotalCount);
        }
        var pendingClose = await client.GetAsync("/api/v1/admin/payments/review?kind=SettlementPendingClose");
        Assert.Equal(HttpStatusCode.OK, pendingClose.StatusCode);
        using var review = await pendingClose.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(1, review!.RootElement.GetProperty("totalCount").GetInt64());
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/transactions/{transactionId}")).StatusCode);
        foreach (var kind in new[] { "AwaitingPayment", "UnmatchedTransaction", "LateTransaction", "ReviewRequiredTransaction" })
        {
            var queue = await client.GetAsync($"/api/v1/admin/payments/review?kind={kind}&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
            using var payload = await queue.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal(1, payload!.RootElement.GetProperty("totalCount").GetInt64());
            Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        }
    }

    [Fact]
    public async Task Admin_resolves_valid_unmatched_transaction_once_and_replay_does_not_repay_invoice()
    {
        var now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 10_000m).Value;
        var invoice = Invoice.Create(Guid.NewGuid(), "ORD-RESOLVE-API", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [item], "DPR-RESOLVE-API", "MBBank", "0383799632", now).Value;
        var bankTransaction = PaymentTransaction.Create(
            "SEPAY", "RESOLVE-001", "SEPAY:RESOLVE-001", "MBBank", "0383799632", null,
            "in", invoice.TotalAmount, null, now, null, null, null, null, null, null, null,
            PaymentTransactionStatus.Unmatched, PaymentTransactionReason.MissingPaymentCode, now);
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(invoice);
            db.PaymentTransactions.Add(bankTransaction);
            await db.SaveChangesAsync();
        }

        var url = $"/api/v1/admin/payments/unmatched/{bankTransaction.Id}/resolve";
        var request = new ResolveUnmatchedTransactionRequest(
            invoice.InvoiceRef, "resolve-unmatched-001", bankTransaction.ProviderTransactionId,
            "Đã đối chiếu giao dịch ngân hàng với hóa đơn.");
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("cus"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, request)).StatusCode);

        var adminId = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm", adminId));
        var first = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await first.Content.ReadFromJsonAsync<ResolveUnmatchedTransactionResponse>();
        Assert.NotNull(firstResult);
        Assert.False(firstResult.IsReplay);
        Assert.Equal(adminId, firstResult.ResolvedBy);

        var replay = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<ResolveUnmatchedTransactionResponse>())!.IsReplay);
        var changedPayload = request with { InvoiceRef = "ANOTHER-INVOICE" };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, changedPayload)).StatusCode);
        var changedKey = request with { OperationKey = "resolve-unmatched-002" };
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, changedKey)).StatusCode);

        await using var verify = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
        Assert.Equal(InvoiceStatus.Paid, (await verify.Invoices.SingleAsync(i => i.Id == invoice.Id)).Status);
        Assert.Single(await verify.PaymentTransactionResolutions.ToListAsync());
        Assert.Single(await verify.OutboxMessages.ToListAsync());
        var queue = await client.GetAsync("/api/v1/admin/payments/review?kind=UnmatchedTransaction");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        using var queueBody = await queue.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal(0, queueBody!.RootElement.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData("late")]
    [InlineData("amount")]
    [InlineData("reference")]
    public async Task Invalid_unmatched_resolution_keeps_invoice_and_transaction_unresolved(string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        var createdAt = scenario == "late" ? now.AddHours(-1) : now;
        var deadline = scenario == "late" ? now.AddMinutes(-30) : now.AddMinutes(30);
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 10_000m).Value;
        var invoice = Invoice.Create(Guid.NewGuid(), $"ORD-RESOLVE-{Guid.NewGuid():N}", Guid.NewGuid(), "VND",
            deadline, [item], $"DPR-{Guid.NewGuid().ToString("N")[..12]}", "MBBank", "0383799632", createdAt).Value;
        var transaction = PaymentTransaction.Create(
            "SEPAY", $"RESOLVE-{Guid.NewGuid():N}", $"SEPAY:{Guid.NewGuid():N}",
            "MBBank", "0383799632", null, "in",
            scenario == "amount" ? invoice.TotalAmount - 1 : invoice.TotalAmount,
            null, scenario == "late" ? now.AddMinutes(-40) : now,
            null, null, null, null, null, null, null,
            PaymentTransactionStatus.Unmatched, PaymentTransactionReason.MissingPaymentCode, now);
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(invoice);
            db.PaymentTransactions.Add(transaction);
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm"));
        var request = new ResolveUnmatchedTransactionRequest(
            invoice.InvoiceRef, $"resolve-{scenario}",
            scenario == "reference" ? "WRONG-REFERENCE" : transaction.ProviderTransactionId,
            "Đối chiếu nhưng dữ liệu không đủ điều kiện ghi nhận.");
        var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/payments/unmatched/{transaction.Id}/resolve", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var verify = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
        Assert.NotEqual(InvoiceStatus.Paid, (await verify.Invoices.SingleAsync(i => i.Id == invoice.Id)).Status);
        Assert.Empty(await verify.PaymentTransactionResolutions.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_unmatched_resolution_requests_create_one_resolution_and_one_event()
    {
        var now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 70_000m, 0m).Value;
        var invoice = Invoice.Create(Guid.NewGuid(), "ORD-CONCURRENT-RESOLVE", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [item], "DPR-CONCURRENT-RESOLVE", "MBBank", "0383799632", now).Value;
        var transaction = PaymentTransaction.Create(
            "SEPAY", "RESOLVE-CONCURRENT", "SEPAY:RESOLVE-CONCURRENT", "MBBank", "0383799632", null,
            "in", invoice.TotalAmount, null, now, null, null, null, null, null, null, null,
            PaymentTransactionStatus.Unmatched, PaymentTransactionReason.MissingPaymentCode, now);
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            db.Invoices.Add(invoice);
            db.PaymentTransactions.Add(transaction);
            await db.SaveChangesAsync();
        }

        var url = $"/api/v1/admin/payments/unmatched/{transaction.Id}/resolve";
        var request = new ResolveUnmatchedTransactionRequest(
            invoice.InvoiceRef, "resolve-concurrent", transaction.ProviderTransactionId,
            "Đã đối chiếu giao dịch với hóa đơn.");
        var token = UserToken("adm", Guid.NewGuid());
        using var firstClient = _factory.CreateClient();
        using var secondClient = _factory.CreateClient();
        firstClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync(url, request), secondClient.PostAsJsonAsync(url, request));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var results = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<ResolveUnmatchedTransactionResponse>()));
        Assert.Single(results, r => r is { IsReplay: false });
        Assert.Single(results, r => r is { IsReplay: true });

        await using var verify = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
        Assert.Single(await verify.PaymentTransactionResolutions.ToListAsync());
        Assert.Single(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Verified_destination_is_versioned_and_required_for_payout_completion()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_connectionString, _integrityConn, _ecdsa);
        var invoice = await harness.SeedAsync();
        var supplierId = invoice.Items[0].SupplierId;
        var otherSupplierId = Guid.NewGuid();
        var item = invoice.Items[0];
        await using var settlementDb = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
        var settled = await harness.Settlement(settlementDb).SettleAsync(
            FinancialWorkflowHarness.SettlementRequest(invoice, item.DepositAmount, "settle-destination-api"), CancellationToken.None);
        Assert.True(settled.IsSuccess, settled.Error.Message);
        Guid payoutId;
        await using (var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString)))
        {
            payoutId = await db.SupplierPayouts.Select(x => x.Id).SingleAsync();
        }

        using var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("adm"));
        var completeUrl = $"/api/v1/admin/payouts/{payoutId}/complete";
        var completion = new CompleteSupplierPayoutRequest("dest-complete-key", "BANK-DEST-001", "Đã đối chiếu chuyển khoản.");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync(completeUrl, completion)).StatusCode);

        var registerUrl = $"/api/v1/admin/suppliers/{supplierId}/payout-destinations";
        var registration = new RegisterPayoutDestinationRequest("dest-register-1", null,
            "VCB", "SUPPLIER NAME", "1234567890", "VERIFY-001", "Đã đối chiếu hồ sơ ngân hàng.");
        var created = await admin.PostAsJsonAsync(registerUrl, registration);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var destination = await created.Content.ReadFromJsonAsync<PayoutDestinationResponse>();
        Assert.NotNull(destination);
        Assert.Equal("****7890", destination.AccountNumberMasked);
        Assert.Equal(1, destination.Version);
        var replay = await admin.PostAsJsonAsync(registerUrl, registration);
        Assert.True((await replay.Content.ReadFromJsonAsync<PayoutDestinationResponse>())!.IsReplay);

        var attachUrl = $"/api/v1/admin/payouts/{payoutId}/destination";
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync(attachUrl, new AttachPayoutDestinationRequest(
                "attach-unknown", "arbitrary-ref", "Không có hồ sơ.", 1))).StatusCode);
        var otherUrl = $"/api/v1/admin/suppliers/{otherSupplierId}/payout-destinations";
        var otherCreated = await admin.PostAsJsonAsync(otherUrl, registration with { OperationKey = "dest-register-other", AccountNumber = "9999999999" });
        Assert.Equal(HttpStatusCode.OK, otherCreated.StatusCode);
        var other = await otherCreated.Content.ReadFromJsonAsync<PayoutDestinationResponse>();
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync(attachUrl, new AttachPayoutDestinationRequest(
                "attach-other", other!.DestinationRef, "Sai supplier.", 1))).StatusCode);

        var attach = new AttachPayoutDestinationRequest("attach-own", destination.DestinationRef,
            "Đã đối chiếu supplier.", destination.Version);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(attachUrl, attach)).StatusCode);
        using (var queue = await admin.GetFromJsonAsync<JsonDocument>("/api/v1/admin/payouts"))
        {
            var row = queue!.RootElement.GetProperty("items")[0];
            Assert.Equal(destination.Id, row.GetProperty("destinationId").GetGuid());
            Assert.Equal(1, row.GetProperty("destinationVersion").GetInt32());
        }
        var newVersion = await admin.PostAsJsonAsync(registerUrl, registration with
        {
            OperationKey = "dest-register-2",
            DestinationRef = destination.DestinationRef,
            AccountNumber = "1234567891",
            VerificationRef = "VERIFY-002"
        });
        Assert.Equal(HttpStatusCode.OK, newVersion.StatusCode);
        Assert.Equal(2, (await newVersion.Content.ReadFromJsonAsync<PayoutDestinationResponse>())!.Version);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(completeUrl, completion)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(completeUrl, completion)).StatusCode);

        await using var verify = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(_connectionString));
        var payout = await verify.SupplierPayouts.SingleAsync();
        Assert.Equal(destination.Id, payout.DestinationId);
        Assert.Equal(SupplierPayout.StatusCompleted, payout.Status);
        Assert.Equal(2, await verify.FinancialOperationAudits.CountAsync());
        Assert.Equal(3, await verify.SupplierPayoutDestinations.CountAsync());
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

    private string UserToken(string role, Guid? userId = null)
    {
        var claims = new[]
        {
            new Claim("sub", (userId ?? Guid.NewGuid()).ToString("D")),
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

    private sealed class MockIntegrityHandler(Func<IntegrityVerificationEngine> engineFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/internal/v1/integrity/verify", StringComparison.Ordinal) == true)
            {
                var reqDto = await request.Content!.ReadFromJsonAsync<TransactionVerificationRequest>(cancellationToken: cancellationToken);
                var engine = engineFactory();
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
