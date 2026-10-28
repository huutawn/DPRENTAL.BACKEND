using System.Security.Cryptography;
using System.Text.Json;
using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.DTOs.Verification;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PaidInvoiceAnalyticsBackfillTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Backfill_preserves_invoice_and_requires_confirmed_matching_evidence(bool confirmed, bool tampered)
    {
        var (_, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var connection = PostgresTestDatabaseHelper.BuildDatabaseConnectionString(PostgresTestDatabaseHelper.BaseConnectionString, dbName);
            await using var db = new PaymentServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connection));
            await db.Database.MigrateAsync();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var encryptor = new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32), "backfill-test");
            var invoice = Invoice.Create(Guid.NewGuid(), "TEST", Guid.NewGuid(), "VND", now.AddDays(1),
                [InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1000, 500).Value], "DPR123456", "TEST", "TEST", now.AddDays(-2)).Value;
            Assert.True(invoice.MarkPaid("backfill-payment", 1500, "VND", now.AddDays(-1)).IsSuccess);
            db.Invoices.Add(invoice);
            var repository = new EfInvoiceRepository(db);
            var preceding = await JournalEntryFactory.CreateNextAsync(repository, Guid.NewGuid(), invoice.InvoiceRef,
                JournalStreamTypes.Invoice, JournalEventTypes.PaymentMatched, invoice, invoice.TotalAmount,
                invoice.Status, "TEST", now.AddDays(-1), [OperationalSnapshotEngine.CreateInvoiceClaim(invoice)], encryptor, null, default);
            if (confirmed) preceding.Confirm("test-proof", "test-key", now);
            db.TransactionJournals.Add(preceding);
            await db.SaveChangesAsync();
            long revision = invoice.Revision;
            var service = new PaidInvoiceAnalyticsBackfillService(repository, new EvidenceChecker(invoice, tampered), encryptor, TimeProvider.System);
            var preview = await service.RunAsync(now, 100, false);
            Assert.Equal(1, preview.Value.Candidates);
            Assert.Equal(0, (await service.RunAsync(now.AddDays(-1), 100, false)).Value.Candidates);
            Assert.True((await service.RunAsync(now, 0, true)).IsFailure);
            Assert.True((await service.RunAsync(now.AddDays(1), 100, true)).IsFailure);
            Assert.Empty(await db.OutboxMessages.ToListAsync());
            if (!confirmed || tampered)
            {
                Assert.True((await service.RunAsync(now, 100, true)).IsFailure);
                Assert.Empty(await db.OutboxMessages.AsNoTracking().ToListAsync());
                return;
            }
            Assert.Equal(1, (await service.RunAsync(now, 100, true)).Value.Enqueued);
            Assert.Equal(0, (await service.RunAsync(now, 100, true)).Value.Candidates);
            var message = await db.OutboxMessages.AsNoTracking().SingleAsync();
            Assert.Equal(OutboxTopics.PaymentAnalyticsV1, message.Topic);
            using var payload = JsonDocument.Parse(message.Payload);
            Assert.Equal(invoice.PaidAt, payload.RootElement.GetProperty("payload").GetProperty("paidAt").GetDateTimeOffset());
            Assert.Equal(1000, payload.RootElement.GetProperty("payload").GetProperty("rentalAmount").GetDecimal());
            var journal = await db.TransactionJournals.AsNoTracking().SingleAsync(j => j.OutboxMessageId == message.MessageId);
            Assert.Equal(preceding.EntryHash, journal.PreviousHash);
            Assert.NotEqual(preceding.OperationId, journal.OperationId);
            Assert.Equal(message.EventId, journal.EventId);
            Assert.False(journal.IsConfirmed);
            await db.Entry(invoice).ReloadAsync();
            Assert.Equal(revision, invoice.Revision);
            Assert.Equal(InvoiceStatus.Paid, invoice.Status);
            Assert.True((await service.RunAsync(now, 101, true)).IsFailure);
        }
        finally { await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_payment_backfill_obtains_real_publication_proof_and_is_concurrent_safe(bool concurrent)
    {
        await using var h = await FinancialWorkflowHarness.CreateAsync();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Invoice invoice;
        await using (var db = new PaymentServiceDbContext(h.PaymentOptions))
        {
            invoice = Invoice.Create(Guid.NewGuid(), "LEGACY", Guid.NewGuid(), "VND", now.AddDays(1),
                [InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 1000, 500).Value],
                "DPR123456", "TEST", "TEST", now.AddDays(-2)).Value;
            Assert.True(invoice.MarkPaid("legacy-bank-reference", 1500, "VND", now.AddDays(-1)).IsSuccess);
            db.Invoices.Add(invoice);
            var repo = new EfInvoiceRepository(db);
            var claims = new[] { OperationalSnapshotEngine.CreateInvoiceClaim(invoice) };
            var invoiceJournal = await JournalEntryFactory.CreateNextAsync(repo, Guid.NewGuid(), invoice.InvoiceRef,
                JournalStreamTypes.Invoice, JournalEventTypes.PaymentMatched, invoice, invoice.TotalAmount,
                InvoiceStatus.Paid, "TEST", now, claims, h.Encryptor, null, default);
            var bankJournal = await JournalEntryFactory.CreateNextAsync(repo, Guid.NewGuid(), invoice.PaymentRef!,
                JournalStreamTypes.PaymentTransaction, JournalEventTypes.BankIncoming, invoice, invoice.TotalAmount,
                "MATCHED", "TEST", now, claims, h.Encryptor, null, default);
            db.TransactionJournals.AddRange(invoiceJournal, bankJournal);
            await db.SaveChangesAsync();
        }
        await h.ConfirmAsync();

        async Task<Result<PaidInvoiceBackfillReport>> RunAsync()
        {
            await using var db = new PaymentServiceDbContext(h.PaymentOptions);
            var service = new PaidInvoiceAnalyticsBackfillService(new EfInvoiceRepository(db), h.Checker(), h.Encryptor, TimeProvider.System);
            return await service.RunAsync(now, 100, true);
        }
        var results = concurrent ? await Task.WhenAll(RunAsync(), RunAsync()) : [await RunAsync()];
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error.Code));
        Assert.Equal(1, results.Sum(r => r.Value.Enqueued));
        Assert.Equal(0, (await RunAsync()).Value.Enqueued);
        await h.ConfirmAsync();
        await using var verify = new PaymentServiceDbContext(h.PaymentOptions);
        var message = await verify.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(3, await verify.TransactionJournals.CountAsync());
        var proof = await h.Checker().VerifyAsync(new TransactionVerificationRequest(
            message.MessageId.ToString("D"), VerificationPurposes.OutboxPublication, Guid.NewGuid().ToString("N"),
            ExpectedOutboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(message.Payload),
            Topic: message.Topic, MessageKey: message.AggregateId, OutboxMessageId: message.MessageId));
        Assert.True(proof.IsSuccess, proof.Error.Code);
        Assert.NotNull(proof.Value.PublicationProof);
        using var payload = JsonDocument.Parse(proof.Value.PublicationProof.Payload);
        Assert.Equal(OutboxTopics.AnalyticsAudience, payload.RootElement.GetProperty("audience").GetString());
        var actual = await verify.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(invoice.Revision, actual.Revision);
        Assert.Equal(invoice.PaidAt, actual.PaidAt);
    }

    private sealed class EvidenceChecker(Invoice invoice, bool tampered) : ITransactionIntegrityChecker
    {
        public Task<Result<TransactionVerificationResponse>> VerifyAsync(TransactionVerificationRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(invoice.PaymentRef, request.PaymentRef);
            Assert.Equal(invoice.InvoiceRef, request.InvoiceRef);
            var claim = OperationalSnapshotEngine.CreateInvoiceClaim(invoice);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var body = new VerificationReceiptBody(1, "test", "payment-api", "test", Guid.NewGuid(), "test", "test",
                request.CallerIdentity!, request.CallerNonce, request.Purpose, IntegrityStatuses.Valid, true, "OK",
                invoice.TotalAmount, invoice.Currency, invoice.Status,
                [new VerificationEntityClaim(claim.EntityType, claim.EntityId, claim.Revision, tampered ? "wrong" : claim.Digest)],
                now, now.AddMinutes(1));
            return Task.FromResult<Result<TransactionVerificationResponse>>(new TransactionVerificationResponse(
                new TransactionIntegrityResult(IntegrityStatuses.Valid, 1, "SYSTEM", now, "test", "test", "test", request.CallerNonce),
                new TransactionUsabilityResult(true, "OK", invoice.TotalAmount, invoice.Currency, invoice.Status),
                new VerificationReceipt(body, "test")));
        }
    }
}
