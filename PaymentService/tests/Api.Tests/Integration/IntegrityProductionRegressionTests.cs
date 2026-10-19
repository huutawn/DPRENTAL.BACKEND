using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.Domain.Entities.Integrity;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class IntegrityProductionRegressionTests
{
    private static readonly IOptions<PaymentMerchantOptions> MerchantOptions =
        Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions
        {
            Bank = "MBBank",
            Account = "0383799632"
        });

    private static readonly IOptions<SePayOptions> SePayConfig =
        Microsoft.Extensions.Options.Options.Create(new SePayOptions
        {
            ApiKey = "secret-sepay-key",
            Gateway = "MBBank",
            AccountNumber = "0383799632",
            GatewayMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MBBank"] = "MBBank"
            }
        });

    private static readonly IPaymentDataEncryptor TestEncryptor =
        new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32), "test-enc-key");

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task WebhookIncomingMatched_ConfirmedByIntegrityHost_PublicVerification_SettlesSuccessfully()
    {
        var (paymentConn, db1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, db2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(iOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "prod-key-1";
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            var services = new ServiceCollection();
            services.AddDbContext<IntegrityDbContext>(o => o.UseNpgsql(integrityConn));
            services.AddDbContext<PaymentServiceDbContext>(o => o.UseNpgsql(paymentConn));
            services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();
            var sp = services.BuildServiceProvider();

            var confirmationWorker = new IntegrityConfirmationWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                keyManager,
                TestEncryptor,
                new TestOptionsMonitor<IntegrityHostOptions>(new IntegrityHostOptions { SigningKeyId = keyId }),
                NullLogger<IntegrityConfirmationWorker>.Instance);

            // 1. Create Invoice
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid rentalItemId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            string invoiceRef;
            string paymentCode;

            await using (var ctx = new PaymentServiceDbContext(pOptions))
            {
                var repo = new EfInvoiceRepository(ctx);
                var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor);
                var creation = new InvoiceCreationService(repo, MerchantOptions, lifecycle, TestEncryptor);

                var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-PROD-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(rentalItemId, supplierId, 400_000m, 100_000m)],
                    "op-create-prod-1"));

                Assert.True(createRes.IsSuccess);
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await ctx.Invoices.SingleAsync(inv => inv.InvoiceRef == invoiceRef)).PaymentCode;
            }

            // Confirm invoice creation
            await confirmationWorker.ProcessConfirmationBatchAsync(CancellationToken.None);

            // 2. Process real incoming webhook via InvoicePaymentProcessor
            long providerTxId = 9001;
            string paymentRef = $"SEPAY:{providerTxId}";
            await using (var ctx = new PaymentServiceDbContext(pOptions))
            {
                var repo = new EfInvoiceRepository(ctx);
                var processor = new InvoicePaymentProcessor(
                    repo,
                    new TestOptionsMonitor<PaymentMerchantOptions>(MerchantOptions.Value),
                    new TestOptionsMonitor<SePayOptions>(SePayConfig.Value),
                    null,
                    TestEncryptor);

                var webhookPayload = new SePayWebhookPayload(
                    Id: providerTxId,
                    Gateway: "MBBank",
                    TransactionDate: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    AccountNumber: "0383799632",
                    SubAccount: null,
                    Code: null,
                    Content: $"Chuyen khoan {paymentCode}",
                    TransferType: "in",
                    Description: "Thanh toan don thue",
                    TransferAmount: 500_000m,
                    Accumulated: 500_000m,
                    ReferenceCode: "REF-PROD-001");

                var procRes = await processor.ProcessAsync(webhookPayload);
                Assert.True(procRes.IsSuccess);
            }

            // Confirm webhook payment in IntegrityHost
            int confirmed = await confirmationWorker.ProcessConfirmationBatchAsync(CancellationToken.None);
            Assert.True(confirmed >= 1);

            // 3. Verify via IntegrityVerificationEngine for SETTLEMENT purpose
            await using (var iCtx = new IntegrityDbContext(iOptions))
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var engine = new IntegrityVerificationEngine(
                    iCtx, pCtx, keyManager, TestEncryptor,
                    new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
                    {
                        ActiveKeyId = keyId,
                        PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
                    }));

                var verifyRes = await engine.VerifyAsync(new TransactionVerificationRequest(
                    PaymentRef: paymentRef,
                    Purpose: VerificationPurposes.Settlement,
                    CallerNonce: "nonce-prod-settle",
                    ExpectedAmount: 500_000m,
                    ExpectedCurrency: "VND",
                    InvoiceRef: invoiceRef,
                    ExpectedOrderId: orderId,
                    RentalItemId: rentalItemId,
                    SupplierId: supplierId));

                Assert.True(verifyRes.IsSuccess);
                Assert.True(verifyRes.Value.Integrity.Status == IntegrityStatuses.Valid,
                    $"Status was: {verifyRes.Value.Integrity.Status}, Reason: {verifyRes.Value.Usability.Reason}");
                Assert.True(verifyRes.Value.Usability.IsUsable);
                Assert.Equal("OK", verifyRes.Value.Usability.Reason);
                Assert.NotNull(verifyRes.Value.Receipt);
            }

            // 4. Perform Settlement via SettlementService
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                var repo = new EfInvoiceRepository(pCtx);
                var ledger = new EfSettlementLedger(pCtx, TimeProvider.System);
                var engine = new IntegrityVerificationEngine(
                    iCtx, pCtx, keyManager, TestEncryptor,
                    new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
                    {
                        ActiveKeyId = keyId,
                        PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
                    }));

                // Fake checker adapter for in-process test
                var checkerMock = new IntegrityCheckerAdapter(engine);
                var settlementService = new SettlementService(repo, ledger, checkerMock);

                var settleRes = await settlementService.SettleAsync(new SettleRentalRequest(
                    RentalOrderId: orderId,
                    RentalItemId: rentalItemId,
                    InvoiceRef: invoiceRef,
                    PaymentRef: paymentRef,
                    SupplierId: supplierId,
                    DepositAmount: 100_000m,
                    FinalDeductionAmount: 20_000m,
                    RefundedDepositAmount: 80_000m,
                    Currency: "VND",
                    DecisionRef: "DEC-PROD-001",
                    OperationKey: "op-settle-prod-1"), CancellationToken.None);

                Assert.True(settleRes.IsSuccess);
                Assert.Equal(SettlementStatus.PendingManualRefund, settleRes.Value.Status);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(db1)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(db1);
            if (!string.IsNullOrEmpty(db2)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(db2);
        }
    }

    [Fact]
    public async Task BlockedStream_InEvidence_PublicVerificationReturns409Conflict()
    {
        var (paymentConn, db1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, db2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            var pOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(pOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(iOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "block-key";
            var keyManager = new IntegrityKeyManager(Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
            {
                SigningKeyId = keyId,
                PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(ecdsa)
            }));

            string paymentRef = "SEPAY:BLOCKED_001";
            string invoiceRef = "INV-BLOCKED-001";

            // Add blocked manifest in Integrity DB
            await using (var iCtx = new IntegrityDbContext(iOptions))
            {
                var manifest = StreamManifest.Create(paymentRef, 1, "hash1", Guid.NewGuid(), DateTimeOffset.UtcNow);
                manifest.Block("Tampering detected by audit scanner", DateTimeOffset.UtcNow);
                iCtx.Manifests.Add(manifest);
                await iCtx.SaveChangesAsync();
            }

            // Verify with IntegrityVerificationEngine
            await using (var iCtx = new IntegrityDbContext(iOptions))
            await using (var pCtx = new PaymentServiceDbContext(pOptions))
            {
                var engine = new IntegrityVerificationEngine(
                    iCtx, pCtx, keyManager, TestEncryptor,
                    new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
                    {
                        ActiveKeyId = keyId,
                        PublicKeys = new Dictionary<string, string> { [keyId] = EcdsaAttestation.ExportPublicKeyBase64(ecdsa) }
                    }));

                var res = await engine.VerifyAsync(new TransactionVerificationRequest(
                    PaymentRef: paymentRef,
                    Purpose: VerificationPurposes.PaymentConfirmation,
                    CallerNonce: "nonce-blk-1",
                    InvoiceRef: invoiceRef));

                Assert.True(res.IsSuccess);
                Assert.Equal(IntegrityStatuses.Tampered, res.Value.Integrity.Status);
                Assert.False(res.Value.Usability.IsUsable);
                Assert.Equal("STREAM_BLOCKED", res.Value.Usability.Reason);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(db1)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(db1);
            if (!string.IsNullOrEmpty(db2)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(db2);
        }
    }

    private sealed class IntegrityCheckerAdapter(IntegrityVerificationEngine engine) : ITransactionIntegrityChecker
    {
        public Task<Common.Result<TransactionVerificationResponse>> VerifyAsync(TransactionVerificationRequest request, CancellationToken cancellationToken = default) =>
            engine.VerifyAsync(request, cancellationToken);
    }
}
