using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Repository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class InvoiceRepositoryRollbackTests
{
    [Fact]
    public async Task Rollback_DoesNotPersistPartialInvoiceReceiptOrOutbox()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid orderId = Guid.NewGuid();
            Guid renterId = Guid.NewGuid();
            Guid supplierId = Guid.NewGuid();
            Guid itemId = Guid.NewGuid();

            var itemRes = InvoiceItem.Create(itemId, supplierId, 200_000m, 50_000m);
            Assert.True(itemRes.IsSuccess);

            var invoiceRes = Invoice.Create(
                orderId,
                "ORD-ROLLBACK-01",
                renterId,
                "VND",
                now.AddMinutes(30),
                [itemRes.Value],
                "DPR ROLLBACK1",
                "MBBank",
                "0383799632",
                now);
            Assert.True(invoiceRes.IsSuccess);
            var invoice = invoiceRes.Value;

            var receipt = OperationReceipt.Create(
                "op-rollback-01",
                "CreateInvoice",
                "dummy-fingerprint",
                "{}",
                invoice.InvoiceRef,
                orderId,
                now);

            var outbox = OutboxMessage.Create(
                Guid.NewGuid(),
                "InvoiceCreated",
                "RentalOrder",
                orderId.ToString("D"),
                "{}",
                now);
            var journalEntry = JournalEntryFactory.CreateEntry(
                operationId: Guid.NewGuid(),
                streamId: invoice.InvoiceRef,
                streamType: JournalStreamTypes.Invoice,
                version: 1,
                eventId: Guid.NewGuid(),
                eventType: JournalEventTypes.InvoiceCreated,
                amount: invoice.TotalAmount,
                currency: invoice.Currency,
                status: invoice.Status,
                confirmationSource: JournalConfirmationSources.System,
                actor: "SYSTEM",
                timestampUtc: now,
                previousHash: CanonicalJournalHasher.GenesisHash,
                encryptor: new PaymentDataEncryptor(new byte[32]));

            // 1. Begin transaction, save invoice + receipt + outbox, but explicitly ROLLBACK
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var repo = new EfInvoiceRepository(ctx);
                await using var tx = await repo.BeginTransactionAsync();

                await repo.SaveNewInvoiceAsync(invoice, receipt, outbox, journalEntry);

                // Explicit rollback
                await tx.RollbackAsync();
            }

            // 2. Verify with fresh context: NO partial entities in DB
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                Assert.Empty(await verifyCtx.Invoices.ToListAsync());
                Assert.Empty(await verifyCtx.InvoiceItems.ToListAsync());
                Assert.Empty(await verifyCtx.OperationReceipts.ToListAsync());
                Assert.Empty(await verifyCtx.OutboxMessages.ToListAsync());
                Assert.Empty(await verifyCtx.TransactionJournals.ToListAsync());
            }

            // 3. Begin transaction, save, but dispose WITHOUT commit (simulating crash/unhandled exception)
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var repo = new EfInvoiceRepository(ctx);
                await using (var tx = await repo.BeginTransactionAsync())
                {
                    await repo.SaveNewInvoiceAsync(invoice, receipt, outbox, journalEntry);
                    // Dispose without commit
                }
            }

            // 4. Verify again: still completely empty
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                Assert.Empty(await verifyCtx.Invoices.ToListAsync());
                Assert.Empty(await verifyCtx.InvoiceItems.ToListAsync());
                Assert.Empty(await verifyCtx.OperationReceipts.ToListAsync());
                Assert.Empty(await verifyCtx.OutboxMessages.ToListAsync());
                Assert.Empty(await verifyCtx.TransactionJournals.ToListAsync());
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
