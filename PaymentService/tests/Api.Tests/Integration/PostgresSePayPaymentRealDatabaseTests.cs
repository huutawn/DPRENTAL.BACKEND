using Dprental.PaymentService.Api.Tests.TestHelpers;
using System.Security.Cryptography;
using System.Globalization;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PostgresSePayPaymentRealDatabaseTests
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
            ApiKey = "test-sepay-key",
            Gateway = "MBBank",
            AccountNumber = "0383799632",
            GatewayMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MBBank"] = "MBBank",
                ["MB"] = "MBBank"
            }
        });
    private static readonly IPaymentDataEncryptor TestEncryptor =
        new PaymentDataEncryptor(RandomNumberGenerator.GetBytes(32), "test-enc-key");

    private static InvoiceCreationService CreateCreationService(
        IInvoiceRepository repo,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var lifecycle = new InvoiceLifecycleService(repo, null, TestEncryptor, tp);
        return new InvoiceCreationService(repo, MerchantOptions, lifecycle, TestEncryptor, tp);
    }

    private static InvoiceLifecycleService CreateLifecycleService(
        IInvoiceRepository repo,
        TimeProvider? timeProvider = null) =>
        new(repo, null, TestEncryptor, timeProvider ?? TimeProvider.System);

    private static InvoicePaymentProcessor CreateProcessor(
        IInvoiceRepository repo,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var mo = new TestOptionsMonitor<PaymentMerchantOptions>(MerchantOptions.Value);
        var so = new TestOptionsMonitor<SePayOptions>(SePayConfig.Value);
        return new InvoicePaymentProcessor(repo, mo, so, null, TestEncryptor, tp);
    }
    private static SePayWebhookPayload CreateBankTx(
        long id,
        string paymentCode,
        decimal amount = 500_000m,
        DateTimeOffset? txDate = null,
        DateTimeOffset? procDate = null,
        string? content = null)
    {
        var date = txDate ?? DateTimeOffset.UtcNow;
        return new SePayWebhookPayload(
            Id: id,
            Gateway: "MBBank",
            TransactionDate: date.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            AccountNumber: "0383799632",
            SubAccount: null,
            Code: paymentCode,
            Content: content ?? (string.IsNullOrEmpty(paymentCode) ? "Chuyen khoan khong ma" : $"Thanh toan {paymentCode}"),
            TransferType: "in",
            Description: "Payment",
            TransferAmount: amount,
            Accumulated: 10_000_000m,
            ReferenceCode: $"FT-{id}");
    }

    [Fact]
    public async Task SequentialRetry_SameId_ReturnsSavedResult_DoesNotRerunMatching()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-SEQ-001", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-seq-001"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx = CreateBankTx(1001, paymentCode, 500_000m);

            // 1. First execution
            await using (var ctx1 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx1));
                var res1 = await processor.ProcessAsync(tx);
                Assert.True(res1.IsSuccess);
            }

            // 2. Sequential retry with exact same ID and business data
            await using (var ctx2 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx2));
                var res2 = await processor.ProcessAsync(tx);
                Assert.True(res2.IsSuccess);
            }

            // Assert exactly 1 transaction, 1 outbox message, Invoice is PAID
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var txs = await verifyCtx.PaymentTransactions.ToListAsync();
                Assert.Single(txs);
                Assert.Equal(PaymentTransactionStatus.Matched, txs[0].Status);

                var outbox = await verifyCtx.OutboxMessages.ToListAsync();
                Assert.Single(outbox);

                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Paid, inv.Status);
                Assert.Equal("SEPAY:1001", inv.PaymentRef);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DuplicateId_DifferentBusinessData_Returns409_KeepsOriginalRecord()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-DUP-DIFF", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-dup-diff"));
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == createRes.Value.InvoiceRef)).PaymentCode;
            }

            var txOriginal = CreateBankTx(1002, paymentCode, 500_000m);

            // 1. Commit original
            await using (var ctx1 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx1));
                var res1 = await processor.ProcessAsync(txOriginal);
                Assert.True(res1.IsSuccess);
            }

            // 2. Retry with same ID but different amount
            var txConflicting = txOriginal with { TransferAmount = 600_000m };
            await using (var ctx2 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx2));
                var res2 = await processor.ProcessAsync(txConflicting);
                Assert.True(res2.IsFailure);
                Assert.Equal(ErrorType.Conflict, res2.Error.Type);
                Assert.Equal("DUPLICATE_PROVIDER_TRANSACTION", res2.Error.Code);
            }

            // Assert original record is kept unchanged
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var txs = await verifyCtx.PaymentTransactions.ToListAsync();
                Assert.Single(txs);
                Assert.Equal(500_000m, txs[0].TransferAmount);
                Assert.Equal(PaymentTransactionStatus.Matched, txs[0].Status);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Concurrent_SameId_SamePayload_BarrierCoordinated_BothSucceed_OneTransaction_OneOutbox()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-BARRIER-SAME", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-barrier-same"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx = CreateBankTx(3001, paymentCode, 500_000m);
            var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                await using var ctx1 = new PaymentServiceDbContext(options);
                var repo1 = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx1),
                    () =>
                    {
                        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                        return Task.CompletedTask;
                    });
                var processor1 = CreateProcessor(repo1);
                return await processor1.ProcessAsync(tx);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx2 = new PaymentServiceDbContext(options);
                var repo2 = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx2),
                    () =>
                    {
                        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                        return Task.CompletedTask;
                    });
                var processor2 = CreateProcessor(repo2);
                return await processor2.ProcessAsync(tx);
            });

            var results = await Task.WhenAll(task1, task2);

            Assert.All(results, r => Assert.True(r.IsSuccess));

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var txs = await verifyCtx.PaymentTransactions.ToListAsync();
            Assert.Single(txs);
            Assert.Equal(PaymentTransactionStatus.Matched, txs[0].Status);

            var outbox = await verifyCtx.OutboxMessages.ToListAsync();
            Assert.Single(outbox);

            var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
            Assert.Equal(InvoiceStatus.Paid, inv.Status);
            Assert.Equal("SEPAY:3001", inv.PaymentRef);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Concurrent_SameId_ChangedCode_BarrierCoordinated_OneWins_OneConflict_NoSecondEffect()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-BARRIER-DIFF", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-barrier-diff"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx1 = CreateBankTx(3002, paymentCode, 500_000m);
            var tx2 = tx1 with { Code = "DPR CDEFGHJKLM" };

            var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                await using var ctx1 = new PaymentServiceDbContext(options);
                var repo1 = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx1),
                    () =>
                    {
                        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                        return Task.CompletedTask;
                    });
                var processor1 = CreateProcessor(repo1);
                return await processor1.ProcessAsync(tx1);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx2 = new PaymentServiceDbContext(options);
                var repo2 = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx2),
                    () =>
                    {
                        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                        return Task.CompletedTask;
                    });
                var processor2 = CreateProcessor(repo2);
                return await processor2.ProcessAsync(tx2);
            });

            var results = await Task.WhenAll(task1, task2);
            Assert.Equal(1, results.Count(r => r.IsSuccess));
            var conflict = Assert.Single(results, r => r.IsFailure);
            Assert.Equal(ErrorType.Conflict, conflict.Error.Type);
            Assert.Equal("DUPLICATE_PROVIDER_TRANSACTION", conflict.Error.Code);

            await using var verifyCtx = new PaymentServiceDbContext(options);
            var txs = await verifyCtx.PaymentTransactions.ToListAsync();
            Assert.Single(txs);

            var outbox = await verifyCtx.OutboxMessages.ToListAsync();
            if (results[0].IsSuccess)
            {
                Assert.Single(outbox);
            }
            else
            {
                Assert.Empty(outbox);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task DuplicateRetry_OnUnmatchedTransaction_WithChangedCode_ReturnsConflict_DoesNotRerunMatching()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-UNMATCHED-CHANGE", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-unmatched-change"));
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == createRes.Value.InvoiceRef)).PaymentCode;
            }

            // 1. First request arrives with NO code -> saved as UNMATCHED
            var txUnmatched = CreateBankTx(3003, paymentCode: "", content: "Chuyen khoan khong ma");
            await using (var ctx1 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx1));
                var res1 = await processor.ProcessAsync(txUnmatched);
                Assert.True(res1.IsSuccess);
            }

            // Verify stored as UNMATCHED
            await using (var verifyCtx1 = new PaymentServiceDbContext(options))
            {
                var stored = await verifyCtx1.PaymentTransactions.SingleAsync(t => t.ProviderTransactionId == "3003");
                Assert.Equal(PaymentTransactionStatus.Unmatched, stored.Status);
                Assert.Equal(PaymentTransactionReason.MissingPaymentCode, stored.ReasonCode);
                Assert.Equal(0, await verifyCtx1.OutboxMessages.CountAsync());
            }

            // 2. Replay arrives with same ID, but now caller adds a valid code
            var txWithCode = txUnmatched with { Code = paymentCode, Content = $"Thanh toan {paymentCode}" };
            await using (var ctx2 = new PaymentServiceDbContext(options))
            {
                var processor = CreateProcessor(new EfInvoiceRepository(ctx2));
                var res2 = await processor.ProcessAsync(txWithCode);
                Assert.True(res2.IsFailure);
                Assert.Equal(ErrorType.Conflict, res2.Error.Type);
                Assert.Equal("DUPLICATE_PROVIDER_TRANSACTION", res2.Error.Code);
            }

            // Verify status was NOT changed to MATCHED, no Outbox emitted
            await using (var verifyCtx2 = new PaymentServiceDbContext(options))
            {
                var stored = await verifyCtx2.PaymentTransactions.SingleAsync(t => t.ProviderTransactionId == "3003");
                Assert.Equal(PaymentTransactionStatus.Unmatched, stored.Status);
                Assert.Equal(0, await verifyCtx2.OutboxMessages.CountAsync());
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private sealed class BarrierInvoiceRepositoryDecorator(
        IInvoiceRepository inner,
        Func<Task>? onAfterReadProviderTx = null) : IInvoiceRepository
    {
        private int _readCount;

        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default) =>
            inner.GetOperationReceiptAsync(operationKey, cancellationToken);

        public Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
            inner.ExistsByRentalOrderIdAsync(rentalOrderId, cancellationToken);

        public Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            inner.GetInvoiceSnapshotAsync(invoiceRef, cancellationToken);

        public Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default) =>
            inner.IsPaymentRefDuplicateAsync(paymentRef, currentInvoiceRef, cancellationToken);
        public Task<TransactionJournalEntry?> GetLatestJournalEntryAsync(string streamId, CancellationToken cancellationToken = default) =>
            inner.GetLatestJournalEntryAsync(streamId, cancellationToken);


        public Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceWithRowLockAsync(invoiceRef, cancellationToken);

        public async Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(string provider, string providerTransactionId, CancellationToken cancellationToken = default)
        {
            var result = await inner.FindPaymentTransactionWithRowLockAsync(provider, providerTransactionId, cancellationToken);
            if (onAfterReadProviderTx != null && Interlocked.Increment(ref _readCount) == 1)
            {
                await onAfterReadProviderTx();
            }
            return result;
        }

        public Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(string paymentCode, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceByPaymentCodeWithRowLockAsync(paymentCode, cancellationToken);

        public Task SavePaymentTransactionAsync(
            PaymentTransaction transaction,
            Invoice? invoice,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            TransactionJournalEntry? invoiceJournalEntry,
            CancellationToken cancellationToken = default) =>
            inner.SavePaymentTransactionAsync(transaction, invoice, outboxMessage, journalEntry, invoiceJournalEntry, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
            inner.GetPendingExpiredCandidateIdsAsync(utcNow, batchSize, cancellationToken);

        public Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceByIdWithRowLockSkipLockedAsync(id, cancellationToken);

        public Task SaveNewInvoiceAsync(
            Invoice invoice,
            OperationReceipt receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            inner.SaveNewInvoiceAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);

        public Task SaveInvoiceChangesAsync(
            Invoice invoice,
            OperationReceipt? receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            inner.SaveInvoiceChangesAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task TwoTransactions_SameInvoice_FirstWins_SecondBecomesReviewRequired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-TWO-TX", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-two-tx"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx1 = CreateBankTx(1003, paymentCode, 500_000m);
            var tx2 = CreateBankTx(1004, paymentCode, 500_000m);

            // 1. First transaction arrives
            await using (var ctx1 = new PaymentServiceDbContext(options))
            {
                var proc = CreateProcessor(new EfInvoiceRepository(ctx1));
                var res1 = await proc.ProcessAsync(tx1);
                Assert.True(res1.IsSuccess);
            }

            // 2. Second transaction arrives for same invoice
            await using (var ctx2 = new PaymentServiceDbContext(options))
            {
                var proc = CreateProcessor(new EfInvoiceRepository(ctx2));
                var res2 = await proc.ProcessAsync(tx2);
                Assert.True(res2.IsSuccess); // ACKed to SePay, but saved as REVIEW_REQUIRED
            }

            // Assert: exactly 1 Outbox message, Invoice has SEPAY:1003, second transaction is REVIEW_REQUIRED
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var txs = await verifyCtx.PaymentTransactions.OrderBy(t => t.ProviderTransactionId).ToListAsync();
                Assert.Equal(2, txs.Count);
                Assert.Equal(PaymentTransactionStatus.Matched, txs[0].Status);
                Assert.Equal(PaymentTransactionStatus.ReviewRequired, txs[1].Status);
                Assert.Equal(PaymentTransactionReason.InvoiceAlreadyPaid, txs[1].ReasonCode);

                var outbox = await verifyCtx.OutboxMessages.ToListAsync();
                Assert.Single(outbox); // Only 1 Outbox emitted!

                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal("SEPAY:1003", inv.PaymentRef);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
    [Fact]
    public async Task ConcurrentPayments_TwoDifferentIds_SameInvoice_ExactlyOneInvoicePaid_SecondReviewRequired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-CONC-TX", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-conc-tx"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx1 = CreateBankTx(1003, paymentCode, 500_000m);
            var tx2 = CreateBankTx(1004, paymentCode, 500_000m);

            var barrier = new Barrier(2);

            var task1 = Task.Run(async () =>
            {
                await using var ctx = new PaymentServiceDbContext(options);
                var repo = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx),
                    () => { barrier.SignalAndWait(); return Task.CompletedTask; });
                var proc = CreateProcessor(repo);
                return await proc.ProcessAsync(tx1);
            });

            var task2 = Task.Run(async () =>
            {
                await using var ctx = new PaymentServiceDbContext(options);
                var repo = new BarrierInvoiceRepositoryDecorator(
                    new EfInvoiceRepository(ctx),
                    () => { barrier.SignalAndWait(); return Task.CompletedTask; });
                var proc = CreateProcessor(repo);
                return await proc.ProcessAsync(tx2);
            });

            var results = await Task.WhenAll(task1, task2);
            Assert.All(results, r => Assert.True(r.IsSuccess));

            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var txs = await verifyCtx.PaymentTransactions.OrderBy(t => t.ProviderTransactionId).ToListAsync();
                Assert.Equal(2, txs.Count);
                Assert.Contains(txs, t => t.Status == PaymentTransactionStatus.Matched);
                Assert.Contains(txs, t => t.Status == PaymentTransactionStatus.ReviewRequired && t.ReasonCode == PaymentTransactionReason.InvoiceAlreadyPaid);

                var outbox = await verifyCtx.OutboxMessages.ToListAsync();
                Assert.Single(outbox);

                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Paid, inv.Status);
                Assert.True(inv.PaymentRef is "SEPAY:1003" or "SEPAY:1004");
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }


    [Fact]
    public async Task Rollback_ErrorBeforeCommit_LeavesNoPartialData_AndRetrySucceeds()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-ROLLBACK-TEST", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-rollback-test"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx = CreateBankTx(1005, paymentCode, 500_000m);

            // 1. Execute with throwing repository decorator
            await using (var ctxFail = new PaymentServiceDbContext(options))
            {
                var failingRepo = new FaultyInvoiceRepository(new EfInvoiceRepository(ctxFail), shouldThrowOnSave: true);
                var procFail = CreateProcessor(failingRepo);
                await Assert.ThrowsAsync<InvalidOperationException>(() => procFail.ProcessAsync(tx));
            }

            // Verify clean rollback: 0 transactions, invoice is still PENDING, 0 outbox
            await using (var checkCtx = new PaymentServiceDbContext(options))
            {
                Assert.Equal(0, await checkCtx.PaymentTransactions.CountAsync());
                Assert.Equal(0, await checkCtx.OutboxMessages.CountAsync());
                var inv = await checkCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Pending, inv.Status);
                Assert.Null(inv.PaymentRef);
            }

            // 2. Retry succeeds cleanly
            await using (var ctxRetry = new PaymentServiceDbContext(options))
            {
                var procRetry = CreateProcessor(new EfInvoiceRepository(ctxRetry));
                var retryRes = await procRetry.ProcessAsync(tx);
                Assert.True(retryRes.IsSuccess);
            }

            // Verify after retry: 1 transaction, invoice is PAID, 1 outbox
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                Assert.Equal(1, await verifyCtx.PaymentTransactions.CountAsync());
                Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync());
                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Paid, inv.Status);
                Assert.Equal("SEPAY:1005", inv.PaymentRef);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RaceWithCancel_CancelCommitsFirst_PaymentSavedAsManualRefundRequired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            Guid orderId = Guid.NewGuid();
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    orderId, "ORD-RACE-CANCEL", Guid.NewGuid(), "VND", DateTimeOffset.UtcNow.AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-race-cancel"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;

                // Cancel commits first
                var lifecycle = CreateLifecycleService(new EfInvoiceRepository(setupCtx));
                var cancelRes = await lifecycle.CancelInvoiceAsync(
                    invoiceRef,
                    new CancelInvoiceRequest(orderId, invoiceRef, "Renter cancelled", "op-cancel-1"));
                Assert.True(cancelRes.IsSuccess);
            }

            var tx = CreateBankTx(1006, paymentCode, 500_000m);

            // Payment arrives on cancelled invoice
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var proc = CreateProcessor(new EfInvoiceRepository(ctx));
                var res = await proc.ProcessAsync(tx);
                Assert.True(res.IsSuccess);
            }

            // Assert: invoice kept Cancelled, PaymentRef recorded, transaction is MANUAL_REFUND_REQUIRED, 1 Outbox emitted
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Cancelled, inv.Status);
                Assert.Equal("SEPAY:1006", inv.PaymentRef);
                Assert.NotNull(inv.PaidAt);

                var txRecord = await verifyCtx.PaymentTransactions.SingleAsync();
                Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, txRecord.Status);
                Assert.Equal(PaymentTransactionReason.LatePayment, txRecord.ReasonCode);

                var outbox = await verifyCtx.OutboxMessages.SingleAsync();
                Assert.Equal("InvoicePaid", outbox.MessageType);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task RaceWithExpiry_ExpiryCommitsFirst_PaymentSavedAsManualRefundRequired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            string invoiceRef;
            string paymentCode;
            var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx), fakeTime);
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-RACE-EXP", Guid.NewGuid(), "VND", fakeTime.GetUtcNow().AddMinutes(30),
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-race-exp"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;

                // Advance time past deadline and run expiry worker batch
                fakeTime.Advance(TimeSpan.FromMinutes(35));
                var lifecycle = CreateLifecycleService(new EfInvoiceRepository(setupCtx), fakeTime);
                int expiredCount = await lifecycle.ExpirePendingInvoicesBatchAsync(10);
                Assert.Equal(1, expiredCount);
            }

            var tx = CreateBankTx(1007, paymentCode, 500_000m, txDate: fakeTime.GetUtcNow());

            // Payment arrives on expired invoice
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var proc = CreateProcessor(new EfInvoiceRepository(ctx), fakeTime);
                var res = await proc.ProcessAsync(tx);
                Assert.True(res.IsSuccess);
            }

            // Assert: invoice kept Expired, PaymentRef recorded, transaction is MANUAL_REFUND_REQUIRED, 1 Outbox emitted
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Expired, inv.Status);
                Assert.Equal("SEPAY:1007", inv.PaymentRef);
                Assert.NotNull(inv.PaidAt);

                var txRecord = await verifyCtx.PaymentTransactions.SingleAsync();
                Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, txRecord.Status);
                Assert.Equal(PaymentTransactionReason.LatePayment, txRecord.ReasonCode);

                var outbox = await verifyCtx.OutboxMessages.SingleAsync();
                Assert.Equal("InvoicePaid", outbox.MessageType);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task LatePayment_TransferBeforeDeadline_ProcessedAfterDeadline_SavedAsManualRefundRequired()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var initCtx = new PaymentServiceDbContext(options))
            {
                await initCtx.Database.MigrateAsync();
            }

            var startTime = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
            var deadline = startTime.AddMinutes(30);
            var transferTime = startTime.AddMinutes(15); // Transfer happened on time!
            var processingTime = startTime.AddMinutes(40); // Webhook arrived after deadline!

            var fakeTime = new FakeTimeProvider(processingTime);

            string invoiceRef;
            string paymentCode;
            await using (var setupCtx = new PaymentServiceDbContext(options))
            {
                var invoiceService = CreateCreationService(new EfInvoiceRepository(setupCtx), new FakeTimeProvider(startTime));
                var createRes = await invoiceService.CreateInvoiceAsync(new CreateInvoiceRequest(
                    Guid.NewGuid(), "ORD-LATE-PROC", Guid.NewGuid(), "VND", deadline,
                    [new CreateInvoiceItemRequest(Guid.NewGuid(), Guid.NewGuid(), 500_000m, 0m)],
                    "op-late-proc"));
                invoiceRef = createRes.Value.InvoiceRef;
                paymentCode = (await setupCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef)).PaymentCode;
            }

            var tx = CreateBankTx(1008, paymentCode, 500_000m, txDate: transferTime, procDate: processingTime);

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                var proc = CreateProcessor(new EfInvoiceRepository(ctx), fakeTime);
                var res = await proc.ProcessAsync(tx);
                Assert.True(res.IsSuccess);
            }

            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var inv = await verifyCtx.Invoices.SingleAsync(i => i.InvoiceRef == invoiceRef);
                Assert.Equal(InvoiceStatus.Expired, inv.Status);
                Assert.Equal("SEPAY:1008", inv.PaymentRef);
                Assert.Equal(transferTime, inv.PaidAt);

                var txRecord = await verifyCtx.PaymentTransactions.SingleAsync();
                Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, txRecord.Status);
                Assert.Equal(PaymentTransactionReason.LatePayment, txRecord.ReasonCode);

                var outbox = await verifyCtx.OutboxMessages.SingleAsync();
                Assert.Equal("InvoicePaid", outbox.MessageType);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    private sealed class FaultyInvoiceRepository(IInvoiceRepository inner, bool shouldThrowOnSave) : IInvoiceRepository
    {
        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);

        public Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default) =>
            inner.GetOperationReceiptAsync(operationKey, cancellationToken);

        public Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
            inner.ExistsByRentalOrderIdAsync(rentalOrderId, cancellationToken);

        public Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            inner.GetInvoiceSnapshotAsync(invoiceRef, cancellationToken);

        public Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default) =>
            inner.IsPaymentRefDuplicateAsync(paymentRef, currentInvoiceRef, cancellationToken);
        public Task<TransactionJournalEntry?> GetLatestJournalEntryAsync(string streamId, CancellationToken cancellationToken = default) =>
            inner.GetLatestJournalEntryAsync(streamId, cancellationToken);


        public Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceWithRowLockAsync(invoiceRef, cancellationToken);

        public Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(string provider, string providerTransactionId, CancellationToken cancellationToken = default) =>
            inner.FindPaymentTransactionWithRowLockAsync(provider, providerTransactionId, cancellationToken);

        public Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(string paymentCode, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceByPaymentCodeWithRowLockAsync(paymentCode, cancellationToken);

        public Task SavePaymentTransactionAsync(
            PaymentTransaction transaction,
            Invoice? invoice,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            TransactionJournalEntry? invoiceJournalEntry,
            CancellationToken cancellationToken = default)
        {
            if (shouldThrowOnSave)
            {
                throw new InvalidOperationException("Simulated database failure before commit.");
            }
            return inner.SavePaymentTransactionAsync(transaction, invoice, outboxMessage, journalEntry, invoiceJournalEntry, cancellationToken);
        }

        public Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
            inner.GetPendingExpiredCandidateIdsAsync(utcNow, batchSize, cancellationToken);

        public Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.FindInvoiceByIdWithRowLockSkipLockedAsync(id, cancellationToken);

        public Task SaveNewInvoiceAsync(
            Invoice invoice,
            OperationReceipt receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            inner.SaveNewInvoiceAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);

        public Task SaveInvoiceChangesAsync(
            Invoice invoice,
            OperationReceipt? receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            inner.SaveInvoiceChangesAsync(invoice, receipt, outboxMessage, journalEntry, cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
