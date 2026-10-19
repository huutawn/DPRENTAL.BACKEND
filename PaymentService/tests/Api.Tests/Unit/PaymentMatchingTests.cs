using Dprental.PaymentService.Application.DTOs.SePay;
using System.Globalization;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PaymentMatchingTests
{
    private const string ValidGateway = "MBBank";
    private const string ValidAccount = "0383799632";
    private const string ValidCode = "DPR 23456789AB";
    private const string AnotherValidCode = "DPR CDEFGHJKLM";
    private const decimal TotalAmount = 1_500_000m;

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FutureDeadline = FixedNow.AddMinutes(30);

    private static (InvoicePaymentProcessor Processor, FakeInvoiceRepo Repo) CreateProcessor(
        Invoice? existingInvoice = null,
        DateTimeOffset? now = null,
        string? gateway = ValidGateway,
        string? account = ValidAccount)
    {
        var repo = new FakeInvoiceRepo(existingInvoice);
        var merchantOptions = new TestOptionsMonitor<PaymentMerchantOptions>(new PaymentMerchantOptions
        {
            Bank = "MBBank",
            Account = "0383799632"
        });
        var sePayOptions = new TestOptionsMonitor<SePayOptions>(new SePayOptions
        {
            ApiKey = "secret-key",
            Gateway = "MBBank",
            AccountNumber = "0383799632",
            GatewayMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MBBank"] = "MBBank",
                ["MB"] = "MBBank"
            }
        });

        var tp = new FakeTimeProvider(now ?? FixedNow);
        var processor = new InvoicePaymentProcessor(repo, merchantOptions, sePayOptions, null, new PaymentDataEncryptor(new byte[32]), tp);
        return (processor, repo);
    }

    private static Invoice CreatePendingInvoice(
        string paymentCode = ValidCode,
        decimal amount = TotalAmount,
        string currency = "VND",
        DateTimeOffset? deadline = null,
        string merchantBank = ValidGateway,
        string merchantAccount = ValidAccount)
    {
        var items = new[]
        {
            InvoiceItem.Create(Guid.NewGuid(), Guid.NewGuid(), amount, 0m).Value
        };

        var inv = Invoice.Create(
            Guid.NewGuid(),
            "ORD-001",
            Guid.NewGuid(),
            currency,
            deadline ?? FutureDeadline,
            items,
            paymentCode,
            merchantBank,
            merchantAccount,
            FixedNow,
            "INV-001").Value;

        return inv;
    }

    private static SePayWebhookPayload CreateTx(
        long id = 100,
        string? code = ValidCode,
        string? content = $"Thanh toan {ValidCode} chuyen khoan",
        string gateway = ValidGateway,
        string account = ValidAccount,
        string transferType = "in",
        decimal amount = TotalAmount,
        DateTimeOffset? txDate = null,
        string? txDateString = null)
    {
        var date = txDate ?? FixedNow.AddMinutes(5);
        string dateString = txDateString ?? date.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new SePayWebhookPayload(
            Id: id,
            Gateway: gateway,
            TransactionDate: dateString,
            AccountNumber: account,
            SubAccount: null,
            Code: code,
            Content: content,
            TransferType: transferType,
            Description: "Chuyen khoan",
            TransferAmount: amount,
            Accumulated: 10_000_000m,
            ReferenceCode: $"REF-{id}");
    }

    [Fact]
    public async Task ProcessAsync_NullPayload_ReturnsValidationError_DoesNotCallRepository()
    {
        var (processor, repo) = CreateProcessor();
        var result = await processor.ProcessAsync(null);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_PAYLOAD", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Fact]
    public async Task ProcessAsync_NullId_ReturnsValidationError_DoesNotCallRepository()
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { Id = null };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_ID", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(-100L)]
    public async Task ProcessAsync_InvalidId_ReturnsValidationError_DoesNotCallRepository(long invalidId)
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { Id = invalidId };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_ID", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Fact]
    public async Task ProcessAsync_NullAmount_ReturnsValidationError_DoesNotCallRepository()
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { TransferAmount = null };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_TRANSFER_AMOUNT", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-500")]
    [InlineData("100.5")]
    [InlineData("1000.99")]
    public async Task ProcessAsync_InvalidAmount_ReturnsValidationError_DoesNotCallRepository(string amountStr)
    {
        decimal invalidAmount = decimal.Parse(amountStr, CultureInfo.InvariantCulture);
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { TransferAmount = invalidAmount };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_TRANSFER_AMOUNT", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_EmptyGateway_ReturnsValidationError_DoesNotCallRepository(string? gateway)
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { Gateway = gateway };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_GATEWAY", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_EmptyAccountNumber_ReturnsValidationError_DoesNotCallRepository(string? account)
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { AccountNumber = account };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_ACCOUNT_NUMBER", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessAsync_EmptyTransferType_ReturnsValidationError_DoesNotCallRepository(string? transferType)
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { TransferType = transferType };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_TRANSFER_TYPE", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-datetime")]
    [InlineData("2024-99-99 99:99:99")]
    public async Task ProcessAsync_InvalidTransactionDate_ReturnsValidationError_DoesNotCallRepository(string? invalidDate)
    {
        var (processor, repo) = CreateProcessor();
        var payload = CreateTx() with { TransactionDate = invalidDate };
        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("INVALID_TRANSACTION_DATE", result.Error.Code);
        Assert.False(repo.TransactionStarted);
    }

    [Fact]
    public async Task ProcessAsync_ValidPayload_ConvertsVietnamTimeToUtc_AndPreservesRawCodeAndContent()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var payload = CreateTx(
            code: "DPR 23456789AB",
            content: "  DPR 23456789AB   chuyen khoan  ",
            txDateString: "2026-10-15 17:15:00");

        var result = await processor.ProcessAsync(payload);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        var expectedUtc = new DateTimeOffset(2026, 10, 15, 10, 15, 0, TimeSpan.Zero);
        Assert.Equal(expectedUtc, repo.SavedTransaction.TransactionDate);
        Assert.Equal("DPR 23456789AB", repo.SavedTransaction.ProviderCode);
        Assert.Equal("  DPR 23456789AB   chuyen khoan  ", repo.SavedTransaction.Content);
    }

    [Fact]
    public async Task ProcessAsync_ExactPayment_MatchesAndEmitsInvoicePaid()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx();
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.Matched, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.ExactMatch, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal($"SEPAY:{tx.Id}", invoice.PaymentRef);
        Assert.NotNull(repo.SavedOutbox);
        Assert.Equal("InvoicePaid", repo.SavedOutbox.MessageType);
    }

    [Fact]
    public async Task ProcessAsync_MissingPaymentCode_SetsUnmatched_DoesNotConfirm()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        // Neither code nor content contains a valid code
        var tx = CreateTx(code: null, content: "Chuyen khoan khong co ma");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.MissingPaymentCode, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_AmbiguousPaymentCode_MultipleInContent_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        // Content contains two distinct valid codes
        var tx = CreateTx(code: null, content: $"Chuyen tien {ValidCode} va {AnotherValidCode}");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AmbiguousPaymentCode, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_ConflictingPaymentCode_CodeFieldDiffersFromContent_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        // Code field says AnotherValidCode, but content says ValidCode
        var tx = CreateTx(code: AnotherValidCode, content: $"Chuyen tien {ValidCode}");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AmbiguousPaymentCode, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_AmountMismatch_Underpayment_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(amount: TotalAmount - 1000m);
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AmountMismatch, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_AmountMismatch_Overpayment_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(amount: TotalAmount + 1000m);
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AmountMismatch, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_AccountMismatch_DifferentAccountNumber_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(account: "9999999999");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AccountMismatch, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_AccountMismatch_UnmappedGateway_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(gateway: "UnknownBank");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.AccountMismatch, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_UnknownInvoice_ValidCodeNotInDatabase_SetsUnmatched()
    {
        // No invoice in DB for this code
        var (processor, repo) = CreateProcessor(existingInvoice: null);

        var tx = CreateTx();
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.InvoiceNotFound, repo.SavedTransaction.ReasonCode);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_OutgoingTransfer_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(transferType: "out");
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.OutgoingTransfer, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_InvoiceAlreadyPaid_DifferentPaymentRef_SetsReviewRequired()
    {
        var invoice = CreatePendingInvoice();
        invoice.MarkPaid("SEPAY:999", TotalAmount, "VND", FixedNow);
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx(id: 100);
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.InvoiceAlreadyPaid, repo.SavedTransaction.ReasonCode);
        Assert.Null(repo.SavedOutbox);
    }

    [Fact]
    public async Task ProcessAsync_OrdinalMatching_RejectsLowercase_AndStrippedSpaces()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        // Lowercase code
        string lowercaseCode = ValidCode.ToLowerInvariant();
        var txLower = CreateTx(code: null, content: $"Thanh toan {lowercaseCode}");
        var resLower = await processor.ProcessAsync(txLower);
        Assert.True(resLower.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Stripped space: "DPR23456789AB"
        string strippedCode = ValidCode.Replace(" ", "");
        var txStripped = CreateTx(id: 101, code: null, content: $"Thanh toan {strippedCode}");
        var resStripped = await processor.ProcessAsync(txStripped);
        Assert.True(resStripped.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Tab separator: "DPR\t23456789AB"
        var txTab = CreateTx(id: 102, code: null, content: "Thanh toan DPR\t23456789AB");
        var resTab = await processor.ProcessAsync(txTab);
        Assert.True(resTab.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Two spaces separator: "DPR  23456789AB"
        var txTwoSpaces = CreateTx(id: 103, code: null, content: "Thanh toan DPR  23456789AB");
        var resTwoSpaces = await processor.ProcessAsync(txTwoSpaces);
        Assert.True(resTwoSpaces.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Newline separator: "DPR\n23456789AB"
        var txNewline = CreateTx(id: 104, code: null, content: "Thanh toan DPR\n23456789AB");
        var resNewline = await processor.ProcessAsync(txNewline);
        Assert.True(resNewline.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Substring inside token prefix: "XYZDPR 23456789AB"
        var txSubPrefix = CreateTx(id: 105, code: null, content: $"Thanh toan XYZ{ValidCode}");
        var resSubPrefix = await processor.ProcessAsync(txSubPrefix);
        Assert.True(resSubPrefix.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Substring inside token suffix: "DPR 23456789ABXYZ"
        var txSubSuffix = CreateTx(id: 106, code: null, content: $"Thanh toan {ValidCode}XYZ");
        var resSubSuffix = await processor.ProcessAsync(txSubSuffix);
        Assert.True(resSubSuffix.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.Unmatched, repo.SavedTransaction!.Status);

        // Code field with leading whitespace: must NOT be trimmed to auto-confirm
        var txCodeLeading = CreateTx(id: 107, code: $" {ValidCode}", content: $"Thanh toan {ValidCode}");
        var resCodeLeading = await processor.ProcessAsync(txCodeLeading);
        Assert.True(resCodeLeading.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction!.Status);
        Assert.Equal(PaymentTransactionReason.AmbiguousPaymentCode, repo.SavedTransaction!.ReasonCode);

        // Code field with trailing whitespace: must NOT be trimmed to auto-confirm
        var txCodeTrailing = CreateTx(id: 108, code: $"{ValidCode} ", content: $"Thanh toan {ValidCode}");
        var resCodeTrailing = await processor.ProcessAsync(txCodeTrailing);
        Assert.True(resCodeTrailing.IsSuccess);
        Assert.Equal(PaymentTransactionStatus.ReviewRequired, repo.SavedTransaction!.Status);
        Assert.Equal(PaymentTransactionReason.AmbiguousPaymentCode, repo.SavedTransaction!.ReasonCode);
    }

    [Fact]
    public async Task ProcessAsync_LatePayment_PaidAtPastDeadline_SetsManualRefundRequired_AndEmitsInvoicePaid()
    {
        var invoice = CreatePendingInvoice();
        var (processor, repo) = CreateProcessor(invoice);

        // Bank transfer occurred after deadline
        var tx = CreateTx(txDate: FutureDeadline.AddSeconds(1));
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.LatePayment, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Expired, invoice.Status);
        Assert.Equal($"SEPAY:{tx.Id}", invoice.PaymentRef);
        Assert.NotNull(repo.SavedOutbox);
        Assert.Equal("InvoicePaid", repo.SavedOutbox.MessageType);
    }

    [Fact]
    public async Task ProcessAsync_LatePayment_ProcessedPastDeadline_SetsManualRefundRequired_AndEmitsInvoicePaid()
    {
        var invoice = CreatePendingInvoice();
        // Bank transfer occurred before deadline, but processor runs after deadline
        var (processor, repo) = CreateProcessor(invoice, now: FutureDeadline.AddMinutes(5));

        var tx = CreateTx(txDate: FixedNow.AddMinutes(10));
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, repo.SavedTransaction.Status);
        Assert.Equal(PaymentTransactionReason.LatePayment, repo.SavedTransaction.ReasonCode);
        Assert.Equal(InvoiceStatus.Expired, invoice.Status);
        Assert.Equal($"SEPAY:{tx.Id}", invoice.PaymentRef);
        Assert.NotNull(repo.SavedOutbox);
        Assert.Equal("InvoicePaid", repo.SavedOutbox.MessageType);
    }

    [Fact]
    public async Task ProcessAsync_LatePayment_CancelledInvoice_PreservesCancelledStatus_SetsManualRefundRequired()
    {
        var invoice = CreatePendingInvoice();
        invoice.Cancel("User cancelled", FixedNow.AddMinutes(5));
        var (processor, repo) = CreateProcessor(invoice);

        var tx = CreateTx();
        var result = await processor.ProcessAsync(tx);

        Assert.True(result.IsSuccess);
        Assert.NotNull(repo.SavedTransaction);
        Assert.Equal(PaymentTransactionStatus.ManualRefundRequired, repo.SavedTransaction.Status);
        Assert.Equal(InvoiceStatus.Cancelled, invoice.Status);
        Assert.Equal($"SEPAY:{tx.Id}", invoice.PaymentRef);
        Assert.NotNull(repo.SavedOutbox);
    }

    private sealed class FakeInvoiceRepo : IInvoiceRepository
    {
        private readonly Invoice? _invoice;
        public PaymentTransaction? SavedTransaction { get; private set; }
        public OutboxMessage? SavedOutbox { get; private set; }
        public bool TransactionStarted { get; private set; }

        public FakeInvoiceRepo(Invoice? invoice)
        {
            _invoice = invoice;
        }

        public Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            TransactionStarted = true;
            return Task.FromResult<IExecutionTransaction>(new FakeTx());
        }

        public Task<OperationReceipt?> GetOperationReceiptAsync(string operationKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<OperationReceipt?>(null);

        public Task<bool> ExistsByRentalOrderIdAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Invoice?> GetInvoiceSnapshotAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            Task.FromResult(_invoice);

        public Task<bool> IsPaymentRefDuplicateAsync(string paymentRef, string currentInvoiceRef, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<TransactionJournalEntry?> GetLatestJournalEntryAsync(string streamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TransactionJournalEntry?>(null);


        public Task<Invoice?> FindInvoiceWithRowLockAsync(string invoiceRef, CancellationToken cancellationToken = default) =>
            Task.FromResult(_invoice);

        public Task<PaymentTransaction?> FindPaymentTransactionWithRowLockAsync(string provider, string providerTransactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PaymentTransaction?>(null);

        public Task<Invoice?> FindInvoiceByPaymentCodeWithRowLockAsync(string paymentCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(_invoice is not null && string.Equals(_invoice.PaymentCode, paymentCode, StringComparison.Ordinal) ? _invoice : null);

        public Task SavePaymentTransactionAsync(
            PaymentTransaction transaction,
            Invoice? invoice,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            TransactionJournalEntry? invoiceJournalEntry,
            CancellationToken cancellationToken = default)
        {
            SavedTransaction = transaction;
            SavedOutbox = outboxMessage;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> GetPendingExpiredCandidateIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<Invoice?> FindInvoiceByIdWithRowLockSkipLockedAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Invoice?>(null);

        public Task SaveNewInvoiceAsync(
            Invoice invoice,
            OperationReceipt receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveInvoiceChangesAsync(
            Invoice invoice,
            OperationReceipt? receipt,
            OutboxMessage? outboxMessage,
            TransactionJournalEntry journalEntry,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private sealed class FakeTx : IExecutionTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
