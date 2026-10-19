using System.Net;
using System.Net.Http.Json;
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

public sealed class IntegrityHostIntegrationTests
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
    public async Task IntegrityHost_ConfirmsJournalEntry_AndVerificationSucceeds()
    {
        var (paymentConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        var paymentOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
        var integrityOptions = new DbContextOptionsBuilder<IntegrityDbContext>()
            .UseNpgsql(integrityConn)
            .Options;

        // Apply migrations / create schema
        await using (var pCtx = new PaymentServiceDbContext(paymentOptions))
        {
            await pCtx.Database.MigrateAsync();
        }

        await using (var iCtx = new IntegrityDbContext(integrityOptions))
        {
            await iCtx.Database.MigrateAsync();
        }

        // Setup services
        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "test-key-v1";
        string privateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(signingKey);
        string publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(signingKey);

        var integrityHostOptions = Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
        {
            SigningKeyId = keyId,
            PrivateKeyBase64 = privateKeyBase64
        });
        var keyManager = new IntegrityKeyManager(integrityHostOptions);

        var services = new ServiceCollection();
        services.AddDbContext<IntegrityDbContext>(o => o.UseNpgsql(integrityConn));
        services.AddDbContext<PaymentServiceDbContext>(o => o.UseNpgsql(paymentConn));
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var confirmationWorker = new IntegrityConfirmationWorker(
            scopeFactory,
            keyManager,
            new TestOptionsMonitor<IntegrityHostOptions>(integrityHostOptions.Value),
            NullLogger<IntegrityConfirmationWorker>.Instance);

        // 1. Create an invoice via InvoiceCreationService
        string invoiceRef;
        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var invoiceRepo = new EfInvoiceRepository(payCtx);
            var lifecycle = new InvoiceLifecycleService(invoiceRepo);
            var creation = new InvoiceCreationService(invoiceRepo, MerchantOptions, lifecycle);

            var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                orderId, "ORD-INT-001", renterId, "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 100_000m)],
                "op-int-create"));

            Assert.True(createRes.IsSuccess);
            invoiceRef = createRes.Value.InvoiceRef;

            // Verify journal entry exists in Payment DB but is unconfirmed
            var unconfirmed = await payCtx.TransactionJournals.SingleAsync(j => j.StreamId == invoiceRef);
            Assert.False(unconfirmed.IsConfirmed);
            Assert.Null(unconfirmed.ProofSignature);
        }

        // 2. Run IntegrityConfirmationWorker
        int confirmedCount = await confirmationWorker.ProcessConfirmationBatchAsync(CancellationToken.None);
        Assert.Equal(1, confirmedCount);

        // 3. Verify in Integrity DB
        await using (var iCtx = new IntegrityDbContext(integrityOptions))
        {
            var attestation = await iCtx.Attestations.SingleAsync(a => a.StreamId == invoiceRef);
            Assert.Equal(1, attestation.Version);
            Assert.Equal(keyId, attestation.KeyId);
            Assert.False(string.IsNullOrWhiteSpace(attestation.Signature));

            var manifest = await iCtx.Manifests.SingleAsync(m => m.StreamId == invoiceRef);
            Assert.Equal(1, manifest.LatestVersion);
            Assert.False(manifest.IsBlocked);
        }

        // 4. Verify in Payment DB: confirmed with proof signature
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var confirmed = await payCtx.TransactionJournals.SingleAsync(j => j.StreamId == invoiceRef);
            Assert.True(confirmed.IsConfirmed);
            Assert.NotNull(confirmed.ProofSignature);
            Assert.Equal(keyId, confirmed.SigningKeyId);

            // 5. Run TransactionIntegrityChecker
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 }
            });
            await using var iCtxVerify = new IntegrityDbContext(integrityOptions);
            var checker = new IntegrityVerificationEngine(iCtxVerify, payCtx, keyManager, verifyOptions);

            var verifyRes = await checker.VerifyAsync(new TransactionVerificationRequest(
                PaymentRef: invoiceRef,
                Purpose: VerificationPurposes.PaymentConfirmation,
                CallerNonce: "nonce-xyz",
                ExpectedAmount: 600_000m,
                ExpectedCurrency: "VND",
                InvoiceRef: invoiceRef,
                ExpectedOrderId: orderId));

            Assert.True(verifyRes.IsSuccess);
            Assert.Equal(IntegrityStatuses.Valid, verifyRes.Value.Integrity.Status);
            Assert.False(verifyRes.Value.Usability.IsUsable);
            Assert.Equal("INVOICE_NOT_PAID", verifyRes.Value.Usability.Reason);
            Assert.Contains(keyId, verifyRes.Value.Integrity.Proof);
        }
    }

    [Fact]
    public async Task TamperDetection_DirectDbEdit_IsDetectedAndRejected()
    {
        var (paymentConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        var paymentOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
        var integrityOptions = new DbContextOptionsBuilder<IntegrityDbContext>()
            .UseNpgsql(integrityConn)
            .Options;

        await using (var pCtx = new PaymentServiceDbContext(paymentOptions)) await pCtx.Database.MigrateAsync();
        await using (var iCtx = new IntegrityDbContext(integrityOptions)) await iCtx.Database.MigrateAsync();

        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "test-key-v1";
        string privateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(signingKey);
        string publicKeyBase64 = EcdsaAttestation.ExportPublicKeyBase64(signingKey);

        var integrityHostOptions = Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
        {
            SigningKeyId = keyId,
            PrivateKeyBase64 = privateKeyBase64
        });
        var keyManager = new IntegrityKeyManager(integrityHostOptions);

        var services = new ServiceCollection();
        services.AddDbContext<IntegrityDbContext>(o => o.UseNpgsql(integrityConn));
        services.AddDbContext<PaymentServiceDbContext>(o => o.UseNpgsql(paymentConn));
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();
        var serviceProvider = services.BuildServiceProvider();

        var confirmationWorker = new IntegrityConfirmationWorker(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            keyManager,
            new TestOptionsMonitor<IntegrityHostOptions>(integrityHostOptions.Value),
            NullLogger<IntegrityConfirmationWorker>.Instance);

        string invoiceRef;
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var invoiceRepo = new EfInvoiceRepository(payCtx);
            var lifecycle = new InvoiceLifecycleService(invoiceRepo);
            var creation = new InvoiceCreationService(invoiceRepo, MerchantOptions, lifecycle);

            var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-TAMPER", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-tamper-create"));
            invoiceRef = createRes.Value.InvoiceRef;
        }

        // Confirm legitimate event
        await confirmationWorker.ProcessConfirmationBatchAsync(CancellationToken.None);

        // 1. Direct SQL edit: tamper EntryHash -> detected as HASH_TAMPERED
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            await payCtx.Database.ExecuteSqlRawAsync(
                "UPDATE transaction_journal SET \"EntryHash\" = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef' WHERE \"StreamId\" = {0}", invoiceRef);
        }

        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 }
            });
            await using var iCtxTamper = new IntegrityDbContext(integrityOptions);
            var checker = new IntegrityVerificationEngine(iCtxTamper, payCtx, keyManager, verifyOptions);

            var verifyRes = await checker.VerifyAsync(new TransactionVerificationRequest(
                PaymentRef: invoiceRef,
                Purpose: VerificationPurposes.PaymentConfirmation,
                CallerNonce: "nonce-tamper",
                InvoiceRef: invoiceRef));

            Assert.True(verifyRes.IsSuccess);
            Assert.Equal(IntegrityStatuses.Tampered, verifyRes.Value.Integrity.Status);
            Assert.False(verifyRes.Value.Usability.IsUsable);
            Assert.True(verifyRes.Value.Usability.Reason is "HASH_TAMPERED" or "ATTESTATION_MISMATCH");
        }

        // 2. Direct SQL edit: restore valid entry hash but tamper signature -> detected as SIGNATURE_INVALID
        string validEntryHash;
        await using (var iCtx = new IntegrityDbContext(integrityOptions))
        {
            var att = await iCtx.Attestations.SingleAsync(a => a.StreamId == invoiceRef);
            validEntryHash = att.EntryHash;
        }

        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            await payCtx.Database.ExecuteSqlRawAsync(
                "UPDATE transaction_journal SET \"EntryHash\" = {0}, \"ProofSignature\" = 'YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXoxMjM0NTY=' WHERE \"StreamId\" = {1}",
                validEntryHash, invoiceRef);
        }
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var verifyOptions = new TestOptionsMonitor<IntegrityVerificationOptions>(new IntegrityVerificationOptions
            {
                ActiveKeyId = keyId,
                PublicKeys = new Dictionary<string, string> { [keyId] = publicKeyBase64 }
            });
            await using var iCtxSig = new IntegrityDbContext(integrityOptions);
            var checker = new IntegrityVerificationEngine(iCtxSig, payCtx, keyManager, verifyOptions);

            var verifyRes = await checker.VerifyAsync(new TransactionVerificationRequest(
                PaymentRef: invoiceRef,
                Purpose: VerificationPurposes.PaymentConfirmation,
                CallerNonce: "nonce-tamper-sig",
                InvoiceRef: invoiceRef));

            Assert.True(verifyRes.IsSuccess);
            Assert.Equal(IntegrityStatuses.Tampered, verifyRes.Value.Integrity.Status);
            Assert.False(verifyRes.Value.Usability.IsUsable);
            Assert.Equal("SIGNATURE_INVALID", verifyRes.Value.Usability.Reason);
        }
    }

    [Fact]
    public async Task PeriodicScanner_DetectsRecordDeletionAndRollback_BlocksStream()
    {
        var (paymentConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        var (integrityConn, _) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        var paymentOptions = PostgresTestDatabaseHelper.CreateOptions(paymentConn);
        var integrityOptions = new DbContextOptionsBuilder<IntegrityDbContext>()
            .UseNpgsql(integrityConn)
            .Options;

        await using (var pCtx = new PaymentServiceDbContext(paymentOptions)) await pCtx.Database.MigrateAsync();
        await using (var iCtx = new IntegrityDbContext(integrityOptions)) await iCtx.Database.MigrateAsync();

        using var signingKey = EcdsaAttestation.CreateEphemeralP256KeyPair();
        string keyId = "test-key-v1";
        var integrityHostOptions = Microsoft.Extensions.Options.Options.Create(new IntegrityHostOptions
        {
            SigningKeyId = keyId,
            PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(signingKey)
        });
        var keyManager = new IntegrityKeyManager(integrityHostOptions);

        var services = new ServiceCollection();
        services.AddDbContext<IntegrityDbContext>(o => o.UseNpgsql(integrityConn));
        services.AddDbContext<PaymentServiceDbContext>(o => o.UseNpgsql(paymentConn));
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var confirmationWorker = new IntegrityConfirmationWorker(
            scopeFactory,
            keyManager,
            new TestOptionsMonitor<IntegrityHostOptions>(integrityHostOptions.Value),
            NullLogger<IntegrityConfirmationWorker>.Instance);

        var scannerWorker = new IntegrityPeriodicScannerWorker(
            scopeFactory,
            new TestOptionsMonitor<IntegrityHostOptions>(integrityHostOptions.Value),
            NullLogger<IntegrityPeriodicScannerWorker>.Instance);

        string invoiceRef;
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            var invoiceRepo = new EfInvoiceRepository(payCtx);
            var lifecycle = new InvoiceLifecycleService(invoiceRepo);
            var creation = new InvoiceCreationService(invoiceRepo, MerchantOptions, lifecycle);

            var createRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
                Guid.NewGuid(), "ORD-ROLLBACK", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                "op-rollback-create"));
            invoiceRef = createRes.Value.InvoiceRef;
        }

        // Confirm
        await confirmationWorker.ProcessConfirmationBatchAsync(CancellationToken.None);

        // Delete record in Payment DB to simulate DB restore from older snapshot / deletion
        await using (var payCtx = new PaymentServiceDbContext(paymentOptions))
        {
            await payCtx.Database.ExecuteSqlRawAsync(
                "DELETE FROM transaction_journal WHERE \"StreamId\" = {0}", invoiceRef);
        }

        // Run Periodic Scanner
        await scannerWorker.ScanAuditHistoryAsync(CancellationToken.None);

        // Verify stream is now BLOCKED and incident recorded
        await using (var iCtx = new IntegrityDbContext(integrityOptions))
        {
            var manifest = await iCtx.Manifests.SingleAsync(m => m.StreamId == invoiceRef);
            Assert.True(manifest.IsBlocked);
            Assert.Contains("missing", manifest.BlockedReason, StringComparison.OrdinalIgnoreCase);

            var incident = await iCtx.Incidents.SingleAsync(i => i.StreamId == invoiceRef);
            Assert.Equal("ROLLBACK_DETECTED", incident.IncidentType);
            Assert.Equal("OPEN", incident.Status);
        }
    }
}
