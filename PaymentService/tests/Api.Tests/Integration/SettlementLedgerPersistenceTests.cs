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
                Assert.Equal(SettlementState.PendingManualRefund, record.Status);
                Assert.Null(record.CompletedAt);
                var refund = await context.ManualRefundRequirements.SingleAsync();
                Assert.Equal(70_000m, refund.Amount);
                Assert.Equal(ManualRefundStatus.PendingManualRefund, refund.Status);
                Assert.Equal(record.RentalItemId, refund.RentalItemId);
                Assert.NotEqual(Guid.Empty, refund.RenterId);
                Assert.False(string.IsNullOrWhiteSpace(refund.PaymentRef));
                Assert.Empty(await context.OutboxMessages.ToListAsync());
                Assert.Single(await context.OperationReceipts.ToListAsync(CancellationToken.None));
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Full_deduction_completes_and_writes_one_rental_settled_event()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: true);
            var settlement = RentalItemSettlement.Create(item, item.DepositAmount, "DEC-FULL", "settle-full").Value;

            await using (var context = new PaymentServiceDbContext(options))
            {
                var result = await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(settlement, CancellationToken.None);
                Assert.True(result.IsSuccess);
                Assert.Equal(SettlementState.Completed, result.Value.State);
                Assert.Null(result.Value.ManualRefundRef);
            }

            await using (var context = new PaymentServiceDbContext(options))
            {
                var replay = await new EfSettlementLedger(context, TimeProvider.System)
                    .RecordSettlementAsync(settlement, CancellationToken.None);
                Assert.True(replay.IsSuccess);
                Assert.Equal(SettlementState.Completed, replay.Value.State);
                Assert.Empty(await context.ManualRefundRequirements.ToListAsync());
                var message = await context.OutboxMessages.SingleAsync();
                Assert.Equal("RentalSettled", message.MessageType);
                using var envelope = System.Text.Json.JsonDocument.Parse(message.Payload);
                Assert.Equal("RentalSettled", envelope.RootElement.GetProperty("messageType").GetString());
                Assert.Equal(0m, envelope.RootElement.GetProperty("payload")
                    .GetProperty("refundedDepositAmount").GetDecimal());
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_manual_refund_confirmations_complete_once_and_emit_one_event()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            InvoiceItem item = await SeedInvoiceAsync(options, paid: true);
            var settlement = RentalItemSettlement.Create(item, 20_000m, "DEC-REFUND", "settle-refund").Value;
            Guid requirementId;
            await using (var db = new PaymentServiceDbContext(options))
            {
                var result = await new EfSettlementLedger(db, TimeProvider.System)
                    .RecordSettlementAsync(settlement, CancellationToken.None);
                Assert.True(result.IsSuccess);
                requirementId = await db.ManualRefundRequirements.Select(x => x.Id).SingleAsync();
            }

            var adminId = Guid.NewGuid();
            async Task<Dprental.PaymentService.Common.Result<Dprental.PaymentService.Domain.IRepository.ManualRefundCompletion>> ConfirmAsync(string reference, string operationKey = "refund-ledger-operation-001")
            {
                await using var db = new PaymentServiceDbContext(options);
                return await new EfManualRefundRepository(db, TimeProvider.System)
                    .CompleteAsync(new Dprental.PaymentService.Domain.IRepository.CompleteManualRefundCommand(
                        requirementId, adminId, operationKey, reference, "Đã đối chiếu chứng từ chuyển khoản."), CancellationToken.None);
            }

            var results = await Task.WhenAll(ConfirmAsync("BANK-123"), ConfirmAsync("BANK-123"));
            Assert.All(results, result => Assert.True(result.IsSuccess));
            Assert.Single(results, result => !result.Value.IsReplay);
            Assert.Single(results, result => result.Value.IsReplay);

            var reusedKeyWithDifferentPayload = await ConfirmAsync("BANK-OTHER");
            Assert.True(reusedKeyWithDifferentPayload.IsFailure);
            Assert.Equal("Payment.ManualRefundPayloadConflict", reusedKeyWithDifferentPayload.Error.Code);

            var conflicting = await ConfirmAsync("BANK-OTHER", "refund-ledger-operation-002");
            Assert.True(conflicting.IsFailure);
            Assert.Equal("Payment.RefundAlreadyCompleted", conflicting.Error.Code);

            await using var verify = new PaymentServiceDbContext(options);
            var refund = await verify.ManualRefundRequirements.SingleAsync();
            var record = await verify.RentalItemSettlements.SingleAsync();
            Assert.Equal(ManualRefundStatus.Completed, refund.Status);
            Assert.Equal(SettlementState.Completed, record.Status);
            Assert.Equal(adminId, refund.CompletedBy);
            var audit = await verify.ManualRefundOperationAudits.SingleAsync();
            Assert.Equal("refund-ledger-operation-001", audit.OperationKey);
            Assert.Equal(adminId, audit.ActorId);
            Assert.Equal("BANK-123", audit.ExternalTransferReference);
            Assert.Equal("Đã đối chiếu chứng từ chuyển khoản.", audit.Reason);
            var message = await verify.OutboxMessages.SingleAsync();
            using var json = System.Text.Json.JsonDocument.Parse(message.Payload);
            Assert.Equal("BANK-123", json.RootElement.GetProperty("payload").GetProperty("refundRef").GetString());
            Assert.Equal(70_000m, json.RootElement.GetProperty("payload").GetProperty("refundedDepositAmount").GetDecimal());
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
