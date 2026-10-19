using System.Security.Cryptography;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Domain.Repository;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Infrastructure.Repositories;
using Dprental.PaymentService.IntegrityHost.Entities;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Persistence;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class IntegrityRegressionFlawTests
{
    private static readonly IOptions<PaymentMerchantOptions> MerchantOptions =
        Microsoft.Extensions.Options.Options.Create(new PaymentMerchantOptions
        {
            Bank = "MBBank",
            Account = "0383799632"
        });

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public void IntegrityHost_MissingPrivateKeyConfig_ShouldFailStartup_NotCreateEphemeralKey()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
        {
            SigningKeyId = "test-key",
            PrivateKeyBase64 = null
        });

        Assert.Throws<InvalidOperationException>(() => new IntegrityKeyManager(options));
    }

    [Fact]
    public async Task EnsureExpiredIfPastDeadlineAsync_MustRecordJournalEntry()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            string invoiceRef;
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var repo = new EfInvoiceRepository(ctx);
                var lifecycle = new InvoiceLifecycleService(repo);
                var creation = new InvoiceCreationService(repo, MerchantOptions, lifecycle);

                var res = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-EXP-REG", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-exp-reg"));
                invoiceRef = res.Value.InvoiceRef;
            }

            DateTimeOffset pastDeadline = deadline.AddMinutes(5);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var repo = new EfInvoiceRepository(ctx);
                var journalRepo = new EfTransactionJournalRepository(ctx);
                var lifecycle = new InvoiceLifecycleService(repo, journalRepo);

                await lifecycle.EnsureExpiredIfPastDeadlineAsync(invoiceRef, pastDeadline, CancellationToken.None);

                var expiredJournal = await ctx.TransactionJournals
                    .FirstOrDefaultAsync(j => j.StreamId == invoiceRef && j.EventType == JournalEventTypes.InvoiceExpired);

                Assert.NotNull(expiredJournal);
                Assert.Equal(InvoiceStatus.Expired, expiredJournal.Status);
                Assert.Equal(2, expiredJournal.Version);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(dbName))
            {
                await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
            }
        }
    }

    [Fact]
    public async Task PendingInvoice_CannotBeUsedForSettlement_ReturnsUsableFalse_InvoiceNotPaid()
    {
        var (paymentConn, dbName1) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, dbName2) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var paymentOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
            var integrityOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(integrityConn).Options;

            await using (var p = new PaymentServiceDbContext(paymentOptions)) await p.Database.MigrateAsync();
            await using (var i = new IntegrityDbContext(integrityOptions)) await i.Database.MigrateAsync();

            using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
            string keyId = "key-reg-1";
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

            var worker = new IntegrityConfirmationWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                keyManager,
                new TestOptionsMonitor<IntegrityHostOptions>(new IntegrityHostOptions { SigningKeyId = keyId }),
                NullLogger<IntegrityConfirmationWorker>.Instance);

            string invoiceRef;
            Guid orderId = Guid.NewGuid();
            await using (var ctx = new PaymentServiceDbContext(paymentOptions))
            {
                var repo = new EfInvoiceRepository(ctx);
                var lifecycle = new InvoiceLifecycleService(repo);
                var creation = new InvoiceCreationService(repo, MerchantOptions, lifecycle);

                var res = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-PEND-SETTLE", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-pend-settle"));
                invoiceRef = res.Value.InvoiceRef;
            }

            // Confirm creation in IntegrityHost
            await worker.ProcessConfirmationBatchAsync(CancellationToken.None);

            // Now test verification engine with purpose SETTLEMENT on this PENDING invoice
            await using (var iCtx = new IntegrityDbContext(integrityOptions))
            await using (var pCtx = new PaymentServiceDbContext(paymentOptions))
            {
                var engine = new IntegrityVerificationEngine(
                    iCtx, pCtx, keyManager,
                    new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions { ActiveKeyId = keyId }));

                var verifyRes = await engine.VerifyAsync(new TransactionVerificationRequest(
                    PaymentRef: invoiceRef,
                    Purpose: VerificationPurposes.Settlement,
                    CallerNonce: "nonce-pend-settle",
                    ExpectedAmount: 500_000m,
                    ExpectedCurrency: "VND",
                    InvoiceRef: invoiceRef,
                    ExpectedOrderId: orderId));

                Assert.True(verifyRes.IsSuccess);
                Assert.False(verifyRes.Value.Usability.IsUsable);
                Assert.Equal("INVOICE_NOT_PAID", verifyRes.Value.Usability.Reason);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(dbName1)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName1);
            if (!string.IsNullOrEmpty(dbName2)) await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName2);
        }
    }

    [Fact]
    public void VerificationReceipt_ReusingSignatureOnNewNonce_FailsVerification()
    {
        using var ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "key-reg-2";

        DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = issuedAt.AddSeconds(5);
        string requestHash = "abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234abcd1234";
        string nonce1 = "original-nonce-001";
        string nonce2 = "replayed-nonce-002"; // Attacker tries to reuse signature with new nonce

        string msg1 = EcdsaAttestation.BuildVerificationReceiptMessage(
            keyId, requestHash, nonce1, "VALID", true, "OK", issuedAt, expiresAt);
        string signature1 = EcdsaAttestation.Sign(ecdsa, msg1);

        // Verification with original nonce passes
        bool validOriginal = EcdsaAttestation.Verify(ecdsa, msg1, signature1);
        Assert.True(validOriginal);

        // Verification with changed nonce fails
        string msg2 = EcdsaAttestation.BuildVerificationReceiptMessage(
            keyId, requestHash, nonce2, "VALID", true, "OK", issuedAt, expiresAt);
        bool validReplay = EcdsaAttestation.Verify(ecdsa, msg2, signature1);
        Assert.False(validReplay);
    }
}
