using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class SettlementLedgerPersistenceTests
{
    [Fact]
    public async Task Paid_invoice_creates_three_entries_and_replays_without_duplicates()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: true);
            var settlementResult = RentalItemSettlement.Create(item, 20_000m, "DEC-001", "settle-op-001");
            Assert.True(settlementResult.IsSuccess);

            await using (var context = new PaymentServiceDbContext(options))
            {
                var result = await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(settlementResult.Value, CancellationToken.None);
                Assert.True(result.IsSuccess);
                Assert.Equal(Dprental.PaymentService.Domain.IRepository.SettlementWriteOutcome.Created, result.Value.Outcome);
            }

            await using (var context = new PaymentServiceDbContext(options))
            {
                var replay = await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(settlementResult.Value, CancellationToken.None);
                Assert.True(replay.IsSuccess);
                Assert.Equal(Dprental.PaymentService.Domain.IRepository.SettlementWriteOutcome.Replayed, replay.Value.Outcome);

                var record = await context.RentalItemSettlements
                    .Include(x => x.Entries)
                    .SingleAsync(CancellationToken.None);
                Assert.Equal(replay.Value.SettlementId, record.Id);
                Assert.Equal(replay.Value.SettlementRef, record.SettlementRef);
                Assert.Equal(10_000m, record.PlatformFeeAmount);
                Assert.Equal(70_000m, record.DepositRefundDue);
                Assert.Equal(110_000m, record.SupplierPayable);
                Assert.Equal(3, record.Entries.Count);
                Assert.Single(await context.OperationReceipts.ToListAsync(CancellationToken.None));
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Conflicting_decision_or_operation_key_does_not_write_again()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: true);
            var first = RentalItemSettlement.Create(item, 20_000m, "DEC-001", "settle-op-001").Value;
            var changedAmount = RentalItemSettlement.Create(item, 30_000m, "DEC-001", "settle-op-001").Value;
            var changedKey = RentalItemSettlement.Create(item, 20_000m, "DEC-001", "settle-op-002").Value;

            await using (var context = new PaymentServiceDbContext(options))
            {
                var created = await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(first, CancellationToken.None);
                Assert.True(created.IsSuccess);
            }

            await using (var context = new PaymentServiceDbContext(options))
            {
                var ledger = new EfSettlementLedger(context, TimeProvider.System);
                var payloadConflict = await ledger.RecordSettlementAsync(changedAmount, CancellationToken.None);
                var keyConflict = await ledger.RecordSettlementAsync(changedKey, CancellationToken.None);
                Assert.True(payloadConflict.IsFailure);
                Assert.True(keyConflict.IsFailure);
                Assert.Single(await context.RentalItemSettlements.ToListAsync(CancellationToken.None));
                Assert.Equal(3, await context.SettlementLedgerEntries.CountAsync(CancellationToken.None));
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Unpaid_invoice_cannot_be_settled()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: false);
            var settlement = RentalItemSettlement.Create(item, 0m, "DEC-001", "settle-op-001").Value;

            await using var context = new PaymentServiceDbContext(options);
            var result = await new EfSettlementLedger(context, TimeProvider.System)
                .RecordSettlementAsync(settlement, CancellationToken.None);
            Assert.True(result.IsFailure);
            Assert.Equal("Payment.InvoiceNotPaid", result.Error.Code);
            Assert.Empty(await context.RentalItemSettlements.ToListAsync(CancellationToken.None));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_retry_creates_only_one_settlement()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: true);
            var settlement = RentalItemSettlement.Create(item, 20_000m, "DEC-001", "settle-op-001").Value;

            async Task<Dprental.PaymentService.Common.Result<Dprental.PaymentService.Domain.IRepository.SettlementWriteResult>> WriteAsync()
            {
                await using var context = new PaymentServiceDbContext(options);
                return await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(settlement, CancellationToken.None);
            }

            var results = await Task.WhenAll(WriteAsync(), WriteAsync());
            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Equal(results[0].Value.SettlementRef, results[1].Value.SettlementRef);

            await using var verify = new PaymentServiceDbContext(options);
            Assert.Equal(1, await verify.RentalItemSettlements.CountAsync());
            Assert.Equal(3, await verify.SettlementLedgerEntries.CountAsync());
            Assert.Equal(1, await verify.OperationReceipts.CountAsync());
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private static async Task<InvoiceItem> SeedInvoiceAsync(
        DbContextOptions<PaymentServiceDbContext> options, bool paid)
    {
        var now = DateTimeOffset.UtcNow;
        var item = InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), 100_000m, 90_000m).Value;
        var invoice = Invoice.Create(
            Guid.NewGuid(), $"ORD-{Guid.NewGuid():N}", Guid.NewGuid(), "VND",
            now.AddMinutes(30), [item], $"DPR {Guid.NewGuid():N}"[..16],
            "MBBank", "0383799632", now).Value;
        if (paid)
        {
            var result = invoice.MarkPaid($"PAY-{Guid.NewGuid():N}", invoice.TotalAmount, "VND", now);
            Assert.True(result.IsSuccess);
        }

        await using var context = new PaymentServiceDbContext(options);
        await context.Database.MigrateAsync(CancellationToken.None);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync(CancellationToken.None);
        return await context.InvoiceItems.AsNoTracking()
            .SingleAsync(CancellationToken.None);
    }
}
