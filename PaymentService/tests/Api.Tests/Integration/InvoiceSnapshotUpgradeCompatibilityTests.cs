using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class InvoiceSnapshotUpgradeCompatibilityTests : IAsyncLifetime
{
    private string _paymentConn = string.Empty;
    private string _paymentDbName = string.Empty;
    private string _integrityConn = string.Empty;
    private string _integrityDbName = string.Empty;
    private readonly ECDsa _ecdsa = EcdsaAttestation.CreateEphemeralP256KeyPair();
    private const string KeyId = "test-upgrade-signing-key";

    public async Task InitializeAsync()
    {
        (_paymentConn, _paymentDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        (_integrityConn, _integrityDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);
        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            await pCtx.Database.MigrateAsync();
        }

        var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;
        await using (var iCtx = new IntegrityDbContext(iOptions))
        {
            await iCtx.Database.MigrateAsync();
        }
    }

    public async Task DisposeAsync()
    {
        _ecdsa.Dispose();
        if (!string.IsNullOrEmpty(_paymentDbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_paymentDbName);
        }
        if (!string.IsNullOrEmpty(_integrityDbName) && await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(_integrityDbName);
        }
    }

    [Fact]
    public async Task LegacySnapshotV1_Invoice_VerifiesSuccessfully_WithoutModifyingEvidenceOrUpgradingVersion()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_paymentConn, _integrityConn, _ecdsa);
        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);
        var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;

        Guid orderId = Guid.NewGuid();
        Guid renterId = Guid.NewGuid();
        string invoiceRef = "INV-V1-" + Guid.NewGuid().ToString("N")[..8];
        string paymentRef = "PAY-V1-" + Guid.NewGuid().ToString("N")[..8];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 100_000m).Value;
        var inv = Invoice.Create(orderId, "ORD-V1", renterId, "VND", now.AddMinutes(30), [item], "DPRV1", "MBBank", "0383799632", now, invoiceRef).Value;
        typeof(Invoice).GetProperty(nameof(Invoice.IntegritySnapshotVersion))!.SetValue(inv, 1);
        inv.MarkPaid(paymentRef, 600_000m, "VND", now);
        typeof(Invoice).GetProperty(nameof(Invoice.IntegritySnapshotVersion))!.SetValue(inv, 1);

        var snap = OperationalSnapshotEngine.CreateInvoiceSnapshot(inv, inv.Revision, 1);
        Assert.False(snap.ContainsKey("cancellationOrigin"));
        var claim = OperationalSnapshotEngine.CreateInvoiceClaim(inv, 1);

        var journal = JournalEntryFactory.CreateEntry(
            operationId: Guid.NewGuid(),
            streamId: inv.InvoiceRef,
            streamType: JournalStreamTypes.Invoice,
            version: 1,
            eventId: Guid.NewGuid(),
            eventType: JournalEventTypes.PaymentMatched,
            amount: inv.TotalAmount,
            currency: inv.Currency,
            status: inv.Status,
            confirmationSource: JournalConfirmationSources.System,
            actor: "SYSTEM",
            timestampUtc: now,
            previousHash: CanonicalJournalHasher.GenesisHash,
            snapshot: snap,
            entityClaims: [claim],
            paymentRef: paymentRef,
            invoiceRef: inv.InvoiceRef,
            rentalOrderId: inv.RentalOrderId,
            encryptor: harness.Encryptor);

        harness.SignJournal(journal, now);
        string originalJournalHash = journal.EntryHash;

        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            pCtx.Invoices.Add(inv);
            pCtx.TransactionJournals.Add(journal);
            await pCtx.SaveChangesAsync();
        }

        await using (var iCtx = new IntegrityDbContext(iOptions))
        {
            string attSig = EcdsaAttestation.Sign(_ecdsa, journal.EntryHash);
            var att = AttestationRecord.Create(journal.StreamId, journal.Version, journal.EventId, journal.EntryHash, journal.PayloadHash, journal.PreviousHash, attSig, KeyId, now);
            iCtx.Attestations.Add(att);
            iCtx.Manifests.Add(StreamManifest.Create(journal.StreamId, journal.Version, journal.EntryHash, journal.EventId, now));
            iCtx.EntityHeads.Add(EntityHeadRecord.Create("Invoice", inv.InvoiceRef, att.Id, 1, inv.Revision, journal.EntryHash, claim.Digest, now));
            await iCtx.SaveChangesAsync();
        }
        // 2. Run verification via IntegrityVerificationEngine
        var keyManager = new IntegrityKeyManager(Options.Create(new IntegrityHostOptions
        {
            SigningKeyId = KeyId,
            PrivateKeyBase64 = EcdsaAttestation.ExportPrivateKeyBase64(_ecdsa)
        }));

        var verifyOptions = new IntegrityVerificationOptions
        {
            ActiveKeyId = KeyId,
            PublicKeys = new Dictionary<string, string> { [KeyId] = EcdsaAttestation.ExportPublicKeyBase64(_ecdsa) }
        };

        await using var pCtxVerify = new PaymentServiceDbContext(pOptions);
        await using var iCtxVerify = new IntegrityDbContext(iOptions);
        var engine = new IntegrityVerificationEngine(iCtxVerify, pCtxVerify, keyManager, harness.Encryptor,
            new TestOptionsMonitor<IntegrityVerificationOptions>(verifyOptions));

        var req = new TransactionVerificationRequest(
            PaymentRef: paymentRef,
            Purpose: VerificationPurposes.Settlement,
            CallerNonce: "nonce-v1-check",
            InvoiceRef: invoiceRef);

        var res = await engine.VerifyAsync(req, CancellationToken.None);
        Assert.True(res.IsSuccess, res.IsFailure ? res.Error.ToString() : "");
        Assert.True(res.Value.Usability.IsUsable, res.Value.Usability.Reason);
        // 3. Verify that invoice remains version 1 and journal bytes are unchanged
        await using var pCtxCheck = new PaymentServiceDbContext(pOptions);
        var refreshedInv = await pCtxCheck.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
        Assert.Equal(1, refreshedInv.IntegritySnapshotVersion);

        var refreshedJournal = await pCtxCheck.TransactionJournals
            .Where(j => j.StreamId == invoiceRef)
            .OrderByDescending(j => j.Version)
            .FirstAsync();
        Assert.Equal(originalJournalHash, refreshedJournal.EntryHash);
    }

    [Fact]
    public async Task LegacySnapshotV1_Invoice_UpgradesToV2_OnValidMutation_PreservingHistoricalBytes()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_paymentConn, _integrityConn, _ecdsa);
        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);

        // 1. Seed invoice with v1
        var invoice = await harness.SeedAsync(paid: false, count: 1, late: false);
        string v1JournalHash;
        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            var inv = await pCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoice.InvoiceRef);
            typeof(Invoice).GetProperty(nameof(Invoice.IntegritySnapshotVersion))!.SetValue(inv, 1);
            await pCtx.SaveChangesAsync();

            var j1 = await pCtx.TransactionJournals.SingleAsync(j => j.StreamId == inv.InvoiceRef);
            v1JournalHash = j1.EntryHash;
        }

        // 2. Perform cancellation with TIMEOUT origin (valid mutation)
        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            var repo = new EfInvoiceRepository(pCtx);
            var journalRepo = new EfTransactionJournalRepository(pCtx);
            var refundRepo = new ManualRefundRepository(pCtx);
            var lifecycle = new InvoiceLifecycleService(repo, journalRepo, harness.Encryptor, refundRepo, harness.Checker());

            var cancelRes = await lifecycle.CancelInvoiceAsync(invoice.InvoiceRef, new CancelInvoiceRequest(
                invoice.RentalOrderId, invoice.InvoiceRef, "Expired timeout", "op-upgrade-cancel", InvoiceCancellationOrigins.Timeout));
            Assert.True(cancelRes.IsSuccess);
        }

        // 3. Verify that mutation upgraded to v2 and preserved v1 journal entry bytes
        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            var mutatedInv = await pCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoice.InvoiceRef);
            Assert.Equal(2, mutatedInv.IntegritySnapshotVersion);
            Assert.Equal(InvoiceCancellationOrigins.Timeout, mutatedInv.CancellationOrigin);
            Assert.Equal(2, mutatedInv.Revision);

            var journals = await pCtx.TransactionJournals
                .Where(j => j.StreamId == invoice.InvoiceRef)
                .OrderBy(j => j.Version)
                .ToListAsync();

            Assert.Equal(2, journals.Count);
            // v1 journal entry is completely untouched
            Assert.Equal(1, journals[0].Version);
            Assert.Equal(v1JournalHash, journals[0].EntryHash);

            // v2 journal links to v1
            Assert.Equal(2, journals[1].Version);
            Assert.Equal(v1JournalHash, journals[1].PreviousHash);
        }
    }

    [Fact]
    public async Task CancellationOrigin_InferredFromReason_IsNeverAllowed_AlwaysStoredAsUnknown()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_paymentConn, _integrityConn, _ecdsa);
        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);

        var invoice = await harness.SeedAsync(paid: false, count: 1, late: false);

        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            var repo = new EfInvoiceRepository(pCtx);
            var journalRepo = new EfTransactionJournalRepository(pCtx);
            var refundRepo = new ManualRefundRepository(pCtx);
            var lifecycle = new InvoiceLifecycleService(repo, journalRepo, harness.Encryptor, refundRepo, harness.Checker());

            // Request contains reason "timeout expired" but CancellationOrigin is NOT sent (null)
            var cancelRes = await lifecycle.CancelInvoiceAsync(invoice.InvoiceRef, new CancelInvoiceRequest(
                invoice.RentalOrderId, invoice.InvoiceRef, "timeout expired reason", "op-no-origin-cancel", null));
            Assert.True(cancelRes.IsSuccess);
        }

        // Verify that CancellationOrigin is UNKNOWN, not inferred as TIMEOUT!
        await using (var pCtx = new PaymentServiceDbContext(pOptions))
        {
            var inv = await pCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoice.InvoiceRef);
            Assert.Equal(InvoiceCancellationOrigins.Unknown, inv.CancellationOrigin);
        }
    }

    [Fact]
    public async Task PreflightClassification_CorrectlyClassifiesInvoicesByEvidence()
    {
        await using var harness = FinancialWorkflowHarness.Attach(_paymentConn, _integrityConn, _ecdsa);
        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);
        var iOptions = new DbContextOptionsBuilder<IntegrityDbContext>().UseNpgsql(_integrityConn).Options;

        // Seed confirmed v2 invoice
        var invV2 = await harness.SeedAsync(paid: true, count: 1, late: false);

        // Seed unconfirmed invoice (pending confirmation)
        var repo = new EfInvoiceRepository(new PaymentServiceDbContext(pOptions), harness.Encryptor);
        var lifecycle = new InvoiceLifecycleService(repo, null, harness.Encryptor, new ManualRefundRepository(new PaymentServiceDbContext(pOptions)), harness.Checker());
        var creation = new InvoiceCreationService(repo, Options.Create(new PaymentMerchantOptions { Bank = "MBBank", Account = "123456789" }), lifecycle, harness.Encryptor);
        var unconfirmedRes = await creation.CreateInvoiceAsync(new CreateInvoiceRequest(
            Guid.NewGuid(), "ORD-PRE-PEND", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
            [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 50_000m)],
            "op-pre-pend"));
        Assert.True(unconfirmedRes.IsSuccess);

        await using var pCtx = new PaymentServiceDbContext(pOptions);
        await using var iCtx = new IntegrityDbContext(iOptions);
        var preflight = new InvoiceEvidencePreflightService(pCtx, iCtx, NullLogger<InvoiceEvidencePreflightService>.Instance);

        var report = await preflight.RunPreflightClassificationAsync(applyVersionUpdates: true, CancellationToken.None);
        Assert.True(report.TotalInvoices >= 2);
        Assert.True(report.Version2Count >= 1);
        Assert.True(report.PendingConfirmationCount >= 1);

        var v2Item = report.Items.First(x => x.InvoiceRef == invV2.InvoiceRef);
        Assert.Equal(InvoiceEvidenceClassification.Version2, v2Item.Classification);
        Assert.Equal(2, v2Item.AssignedVersion);

        var pendingItem = report.Items.First(x => x.InvoiceRef == unconfirmedRes.Value.InvoiceRef);
        Assert.Equal(InvoiceEvidenceClassification.PendingConfirmation, pendingItem.Classification);
    }

    [Fact]
    public async Task Migration_AddIntegritySnapshotVersion_IsIdempotent_OnPostgreSql()
    {
        var pOptions = PostgresTestDatabaseHelper.CreateOptions(_paymentConn);
        await using var pCtx = new PaymentServiceDbContext(pOptions);

        // Execute the migration DDL again on a database that already has the column
        // Up SQL executes idempotent DO block
        await pCtx.Database.ExecuteSqlRawAsync("""
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_name = 'invoices' AND column_name = 'IntegritySnapshotVersion'
                ) THEN
                    ALTER TABLE invoices
                    ADD COLUMN "IntegritySnapshotVersion" integer NOT NULL DEFAULT 1;
                ELSE
                    ALTER TABLE invoices
                    ALTER COLUMN "IntegritySnapshotVersion" SET DATA TYPE integer,
                    ALTER COLUMN "IntegritySnapshotVersion" SET NOT NULL,
                    ALTER COLUMN "IntegritySnapshotVersion" SET DEFAULT 1;
                END IF;
            END $$;
            """);

        // Verify column definition in information_schema
        var columnInfo = await pCtx.Database.SqlQueryRaw<string>("""
            SELECT data_type AS "Value" FROM information_schema.columns
            WHERE table_name = 'invoices' AND column_name = 'IntegritySnapshotVersion'
            """).FirstAsync();

        Assert.Equal("integer", columnInfo);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
