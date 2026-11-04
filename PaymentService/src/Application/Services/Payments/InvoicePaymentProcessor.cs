using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Application.Services.Integrity;
using System.Globalization;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoicePaymentProcessor : IInvoicePaymentProcessor
{
    private const string ProviderName = "SEPAY";

    private readonly IInvoiceRepository _repository;
    private readonly ITransactionJournalRepository? _journalRepository;
    private readonly IOptionsMonitor<PaymentMerchantOptions> _merchantOptions;
    private readonly IOptionsMonitor<SePayOptions> _sePayOptions;
    private readonly IPaymentDataEncryptor _encryptor;
    private readonly TimeProvider _timeProvider;
    private readonly Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? _notifier;
    private readonly ISupplierPayoutRepository? _payouts;
    private readonly ISupplierPayoutService? _payoutService;
    private readonly IManualRefundRepository? _refunds;
    private readonly IManualRefundService? _refundService;
    private readonly ILogger<InvoicePaymentProcessor> _logger;

    // Tiền ra khớp mã DPO/DRF đã ghi sổ; payout/hoàn tiền được hoàn tất ở bước sau khi transaction webhook đã commit.
    private sealed record OutgoingBankMatch(string TransferCode, string PaymentRef);

    private static readonly Action<ILogger, string, string, string, Exception?> LogTransferNotCompleted =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(4101, "OutgoingTransferNotCompleted"),
            "Không tự hoàn tất được {TransferCode} từ giao dịch {PaymentRef}: {ErrorCode}");

    public InvoicePaymentProcessor(
        IInvoiceRepository repository,
        IOptionsMonitor<PaymentMerchantOptions> merchantOptions,
        IOptionsMonitor<SePayOptions> sePayOptions,
        ITransactionJournalRepository? journalRepository,
        IPaymentDataEncryptor encryptor,
        TimeProvider? timeProvider = null,
        Dprental.PaymentService.Application.Hub.IPaymentHubNotifier? notifier = null,
        ISupplierPayoutRepository? payouts = null,
        ISupplierPayoutService? payoutService = null,
        IManualRefundRepository? refunds = null,
        IManualRefundService? refundService = null,
        ILogger<InvoicePaymentProcessor>? logger = null)
    {
        _payouts = payouts;
        _payoutService = payoutService;
        _refunds = refunds;
        _refundService = refundService;
        _logger = logger ?? NullLogger<InvoicePaymentProcessor>.Instance;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _journalRepository = journalRepository;
        _encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _sePayOptions = sePayOptions ?? throw new ArgumentNullException(nameof(sePayOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _notifier = notifier;
    }


    public async Task<Result> ProcessAsync(SePayWebhookPayload? payload, CancellationToken cancellationToken = default)
    {
        var recorded = await RecordAsync(payload, cancellationToken);
        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        // Chạy cả khi webhook bị gửi lại (giao dịch trùng): lần trước có thể đã ghi sổ nhưng chưa kịp hoàn tất.
        if (recorded.Value is { } match)
        {
            await CompleteOutgoingAsync(match, cancellationToken);
        }

        return Result.Success();
    }

    private static bool IsPayoutCode(string code) => code.StartsWith(PaymentCodeGenerator.PayoutPrefix, StringComparison.Ordinal);

    private async Task<OutgoingTransferTarget?> FindTransferTargetAsync(string code, CancellationToken cancellationToken)
    {
        if (IsPayoutCode(code))
        {
            return _payouts is null ? null : await _payouts.FindByTransferCodeAsync(code, cancellationToken);
        }

        return _refunds is null ? null : await _refunds.FindByTransferCodeAsync(code, cancellationToken);
    }

    private async Task CompleteOutgoingAsync(OutgoingBankMatch match, CancellationToken cancellationToken)
    {
        var target = await FindTransferTargetAsync(match.TransferCode, cancellationToken);
        if (target is null)
        {
            return;
        }

        Result? completed = null;
        if (IsPayoutCode(match.TransferCode))
        {
            if (_payoutService is not null)
            {
                completed = await _payoutService.CompleteFromBankAsync(target.Id, match.PaymentRef, cancellationToken);
            }
        }
        else if (_refundService is not null)
        {
            completed = await _refundService.CompleteFromBankAsync(target.Id, match.PaymentRef, cancellationToken);
        }

        // Lỗi ở đây không làm webhook thất bại: giao dịch đã ghi sổ, admin vẫn xác nhận tay được.
        if (completed is { IsFailure: true })
        {
            LogTransferNotCompleted(_logger, match.TransferCode, match.PaymentRef, completed.Error.Code, null);
        }
    }

    private async Task<Result<OutgoingBankMatch?>> RecordAsync(SePayWebhookPayload? payload, CancellationToken cancellationToken)
    {
        // 1. Validate and normalize input before opening any DB transaction
        var normalizeResult = SePayTransactionNormalizer.Normalize(payload);
        if (normalizeResult.IsFailure)
        {
            return normalizeResult.Error;
        }

        NormalizedBankTransaction transaction = normalizeResult.Value;
        string providerTxId = transaction.Id.ToString(CultureInfo.InvariantCulture);
        string paymentRef = $"{ProviderName}:{transaction.Id}";

        // 2. Open transaction, claim provider transaction and handle replay
        await using var tx = await _repository.BeginTransactionAsync(cancellationToken);
        try
        {
            var existingTx = await _repository.FindPaymentTransactionWithRowLockAsync(ProviderName, providerTxId, cancellationToken);
            if (existingTx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return EvaluateDuplicateTransaction(existingTx, transaction);
            }

            // 3. Tiền ra có mã DPO/DRF đi nhánh payout/hoàn tiền, không bao giờ đụng invoice;
            // còn lại extract mã DPR và lấy khóa hàng Invoice khi có đúng một mã.
            var outgoingExtraction = string.Equals(transaction.TransferType, "out", StringComparison.OrdinalIgnoreCase)
                ? PaymentCodeExtractor.ExtractOutgoingCode(transaction.Content, transaction.Description)
                : null;
            bool isOutgoingTransfer = outgoingExtraction is not null && outgoingExtraction.Status != CodeExtractionStatus.Missing;

            CodeExtractionResult extraction;
            Invoice? invoice;
            OutgoingTransferTarget? target = null;
            if (isOutgoingTransfer)
            {
                extraction = outgoingExtraction!;
                invoice = null;
                if (extraction.Status == CodeExtractionStatus.SingleMatch)
                {
                    target = await FindTransferTargetAsync(extraction.Code!, cancellationToken);
                }
            }
            else
            {
                (extraction, invoice) = await ResolveInvoiceAsync(transaction, cancellationToken);
            }

            // 4. Obtain processing time after lock and evaluate matching classification
            DateTimeOffset processingTimeUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            DateTimeOffset bankTimeUtc = transaction.TransactionDate;

            var classification = isOutgoingTransfer
                ? ClassifyOutgoingTransfer(extraction, target, transaction)
                : ClassifyTransaction(
                    extraction,
                    invoice,
                    transaction,
                    paymentRef,
                    bankTimeUtc,
                    processingTimeUtc);

            // 5. Apply domain transition for MATCHED or MANUAL_REFUND_REQUIRED
            var domainResult = ApplyDomainTransition(
                classification.Status,
                invoice,
                paymentRef,
                transaction.TransferAmount,
                bankTimeUtc,
                processingTimeUtc,
                out var invoiceToPersist,
                out var outbox);

            if (domainResult.IsFailure)
            {
                await tx.RollbackAsync(cancellationToken);
                return domainResult.Error;
            }

            // 6. Create PaymentTransaction record
            var pt = PaymentTransaction.Create(
                ProviderName,
                providerTxId,
                paymentRef,
                transaction.Gateway,
                transaction.AccountNumber,
                transaction.SubAccount,
                transaction.TransferType,
                transaction.TransferAmount,
                transaction.Accumulated,
                bankTimeUtc,
                transaction.ReferenceCode,
                transaction.Description,
                transaction.Content,
                providerCode: transaction.Code,
                paymentCode: extraction.Code,
                invoiceId: invoice?.Id,
                invoiceRef: invoice?.InvoiceRef,
                status: classification.Status,
                reasonCode: classification.ReasonCode,
                createdAt: processingTimeUtc);

            Guid operationId = Guid.NewGuid();
            Guid bankEventId = Guid.NewGuid();
            Guid? invoiceEventId = invoiceToPersist is not null ? Guid.NewGuid() : null;

            if (outbox is not null)
            {
                outbox.LinkAttestation(invoiceEventId ?? bankEventId, operationId);
            }
            OutboxMessage? analyticsOutbox = classification.Status == PaymentTransactionStatus.Matched && invoiceToPersist is not null
                ? PaymentOutboxFactory.CreateInvoicePaidFinancialOutboxMessage(invoiceToPersist, processingTimeUtc)
                : null;

            string outboxDigest = CanonicalJournalHasher.ComputeOutboxDigest(outbox?.Payload);
            var claims = new List<CanonicalEntityClaim>
            {
                OperationalSnapshotEngine.CreatePaymentTransactionClaim(pt)
            };
            if (invoiceToPersist is not null)
            {
                claims.Add(OperationalSnapshotEngine.CreateInvoiceClaim(invoiceToPersist));
            }
            if (outbox is not null)
            {
                claims.Add(OperationalSnapshotEngine.CreateOutboxClaim(outbox));
            }
            if (analyticsOutbox is not null)
            {
                claims.Add(OperationalSnapshotEngine.CreateOutboxClaim(analyticsOutbox));
            }

            var prevBank = await _repository.GetLatestJournalEntryAsync(paymentRef, cancellationToken);
            long nextBankVersion = (prevBank?.Version ?? 0) + 1;
            string prevBankHash = prevBank?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

            var bankJournal = JournalEntryFactory.CreateEntry(
                operationId: operationId,
                streamId: paymentRef,
                streamType: JournalStreamTypes.PaymentTransaction,
                version: nextBankVersion,
                eventId: bankEventId,
                eventType: string.Equals(transaction.TransferType, "out", StringComparison.OrdinalIgnoreCase)
                    ? JournalEventTypes.BankOutgoing
                    : JournalEventTypes.BankIncoming,
                amount: transaction.TransferAmount,
                currency: "VND",
                status: classification.Status,
                confirmationSource: JournalConfirmationSources.Webhook,
                actor: "WEBHOOK:SEPAY",
                timestampUtc: bankTimeUtc,
                previousHash: prevBankHash,
                bankTimeUtc: bankTimeUtc,
                relatedEntities: new Dictionary<string, string?>
                {
                    ["paymentRef"] = paymentRef,
                    ["invoiceRef"] = invoice?.InvoiceRef,
                    ["rentalOrderId"] = invoice?.RentalOrderId.ToString("D"),
                    ["gateway"] = transaction.Gateway,
                    ["paymentCode"] = extraction.Code,
                    ["referenceCode"] = transaction.ReferenceCode
                },
                snapshot: OperationalSnapshotEngine.CreatePaymentTransactionSnapshot(pt, pt.Revision),
                entityClaims: claims,
                outboxDigest: outboxDigest,
                outboxMessageId: outbox?.MessageId,
                paymentRef: paymentRef,
                invoiceRef: invoice?.InvoiceRef,
                rentalOrderId: invoice?.RentalOrderId,
                encryptor: _encryptor);

            TransactionJournalEntry? invoiceJournal = null;
            TransactionJournalEntry? analyticsJournal = null;
            if (invoiceToPersist is not null)
            {
                var prevInv = await _repository.GetLatestJournalEntryAsync(invoiceToPersist.InvoiceRef, cancellationToken);
                long nextInvVersion = (prevInv?.Version ?? 0) + 1;
                string prevInvHash = prevInv?.EntryHash ?? CanonicalJournalHasher.GenesisHash;

                invoiceJournal = JournalEntryFactory.CreateEntry(
                    operationId: operationId,
                    streamId: invoiceToPersist.InvoiceRef,
                    streamType: JournalStreamTypes.Invoice,
                    version: nextInvVersion,
                    eventId: invoiceEventId!.Value,
                    eventType: classification.Status == PaymentTransactionStatus.Matched
                        ? JournalEventTypes.PaymentMatched
                        : JournalEventTypes.PaymentUnmatched,
                    amount: invoiceToPersist.TotalAmount,
                    currency: invoiceToPersist.Currency,
                    status: invoiceToPersist.Status,
                    confirmationSource: JournalConfirmationSources.Webhook,
                    actor: "WEBHOOK:SEPAY",
                    timestampUtc: processingTimeUtc,
                    previousHash: prevInvHash,
                    relatedEntities: new Dictionary<string, string?>
                    {
                        ["paymentRef"] = paymentRef,
                        ["invoiceRef"] = invoiceToPersist.InvoiceRef,
                        ["rentalOrderId"] = invoiceToPersist.RentalOrderId.ToString("D")
                    },
                    snapshot: OperationalSnapshotEngine.CreateInvoiceSnapshot(invoiceToPersist, invoiceToPersist.Revision),
                    entityClaims: claims,
                    outboxDigest: outboxDigest,
                    outboxMessageId: outbox?.MessageId,
                    paymentRef: paymentRef,
                    invoiceRef: invoiceToPersist.InvoiceRef,
                    rentalOrderId: invoiceToPersist.RentalOrderId,
                    encryptor: _encryptor);
                if (analyticsOutbox is not null)
                {
                    analyticsJournal = JournalEntryFactory.CreateAnalyticsLinkEntry(
                        invoiceJournal, invoiceToPersist, analyticsOutbox, claims,
                        JournalConfirmationSources.Webhook, "WEBHOOK:SEPAY", processingTimeUtc, _encryptor);
                }
            }
            // 8. Persist atomically and commit
            await _repository.SavePaymentTransactionAsync(pt, invoiceToPersist, outbox, bankJournal, invoiceJournal,
                analyticsOutbox, analyticsJournal, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            if (_notifier is not null && classification.Status == PaymentTransactionStatus.Matched && invoiceToPersist is not null && invoiceJournal is not null)
            {
                await _notifier.NotifyPaymentCompletedAsync(
                    invoiceToPersist.RenterId, invoiceJournal.EventId, invoiceToPersist, cancellationToken);
            }
            return Result.Success(ToOutgoingMatch(pt));
        }
        catch (StorageConstraintViolationException ex)
        {
            await tx.RollbackAsync(cancellationToken);

            if (ex.ViolationType == StorageConstraintViolationType.PaymentTransaction)
            {
                var reloaded = await _repository.FindPaymentTransactionWithRowLockAsync(ProviderName, providerTxId, cancellationToken);
                if (reloaded is not null)
                {
                    return EvaluateDuplicateTransaction(reloaded, transaction);
                }

                return Error.Conflict(
                    "DUPLICATE_PROVIDER_TRANSACTION",
                    "Giao dịch trùng ID nhà cung cấp nhưng dữ liệu nghiệp vụ không khớp.");
            }

            if (ex.ViolationType == StorageConstraintViolationType.PaymentRef)
            {
                return Error.Conflict(
                    "DUPLICATE_PAYMENT_REF",
                    "PaymentRef đã tồn tại trên một hóa đơn khác.");
            }

            throw;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static OutgoingBankMatch? ToOutgoingMatch(PaymentTransaction pt) =>
        pt.Status == PaymentTransactionStatus.Matched &&
        pt.ReasonCode is PaymentTransactionReason.PayoutTransfer or PaymentTransactionReason.RefundTransfer &&
        pt.PaymentCode is { } transferCode
            ? new OutgoingBankMatch(transferCode, pt.PaymentRef)
            : null;

    private static Result<OutgoingBankMatch?> EvaluateDuplicateTransaction(PaymentTransaction existingTx, NormalizedBankTransaction transaction)
    {
        bool matches = existingTx.MatchesBusinessData(
            transaction.Gateway,
            transaction.AccountNumber,
            transaction.SubAccount,
            transaction.TransferType,
            transaction.TransferAmount,
            transaction.TransactionDate,
            transaction.ReferenceCode,
            transaction.Description,
            transaction.Content,
            transaction.Code);

        if (matches)
        {
            return Result.Success(ToOutgoingMatch(existingTx));
        }

        return Error.Conflict(
            "DUPLICATE_PROVIDER_TRANSACTION",
            "Giao dịch trùng ID nhà cung cấp nhưng dữ liệu nghiệp vụ không khớp.");
    }

    // Tiền ra từ tài khoản hệ thống có mã DPO/DRF: chỉ khớp khi payout/hoàn tiền còn chờ và số tiền bằng đúng.
    private MatchingClassification ClassifyOutgoingTransfer(
        CodeExtractionResult extraction,
        OutgoingTransferTarget? target,
        NormalizedBankTransaction transaction)
    {
        if (extraction.Status == CodeExtractionStatus.Ambiguous)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AmbiguousPaymentCode);
        }

        string? configuredAccount = string.IsNullOrWhiteSpace(_sePayOptions.CurrentValue.AccountNumber)
            ? null
            : _sePayOptions.CurrentValue.AccountNumber;
        bool accountMatches = string.Equals(transaction.AccountNumber, _merchantOptions.CurrentValue.Account, StringComparison.Ordinal) &&
                              (configuredAccount is null || string.Equals(transaction.AccountNumber, configuredAccount, StringComparison.Ordinal));
        if (!accountMatches)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AccountMismatch);
        }

        if (target is null)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.TransferTargetNotFound);
        }

        if (!target.IsPending)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.TransferTargetNotPending);
        }

        if (transaction.TransferAmount != target.Amount)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AmountMismatch);
        }

        return new(PaymentTransactionStatus.Matched, IsPayoutCode(extraction.Code!)
            ? PaymentTransactionReason.PayoutTransfer
            : PaymentTransactionReason.RefundTransfer);
    }

    private async Task<(CodeExtractionResult Extraction, Invoice? Invoice)> ResolveInvoiceAsync(
        NormalizedBankTransaction transaction,
        CancellationToken ct)
    {
        var extraction = PaymentCodeExtractor.Extract(transaction.Code, transaction.Content, transaction.Description);
        if (extraction.Status != CodeExtractionStatus.SingleMatch)
        {
            return (extraction, null);
        }

        var invoice = await _repository.FindInvoiceByPaymentCodeWithRowLockAsync(extraction.Code!, ct);
        return (extraction, invoice);
    }

    private MatchingClassification ClassifyTransaction(
        CodeExtractionResult extraction,
        Invoice? invoice,
        NormalizedBankTransaction transaction,
        string paymentRef,
        DateTimeOffset bankTimeUtc,
        DateTimeOffset processingTimeUtc)
    {
        if (extraction.Status == CodeExtractionStatus.Missing)
        {
            return new(PaymentTransactionStatus.Unmatched, PaymentTransactionReason.MissingPaymentCode);
        }

        if (extraction.Status == CodeExtractionStatus.Ambiguous)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AmbiguousPaymentCode);
        }

        if (invoice is null)
        {
            return new(PaymentTransactionStatus.Unmatched, PaymentTransactionReason.InvoiceNotFound);
        }

        bool isIncoming = string.Equals(transaction.TransferType, "in", StringComparison.OrdinalIgnoreCase) && transaction.TransferAmount > 0;
        if (!isIncoming)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.OutgoingTransfer);
        }

        string? mappedBank = ResolveMappedBank(transaction.Gateway);
        bool bankMatches = mappedBank is not null &&
                           string.Equals(mappedBank, invoice.MerchantBank, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(mappedBank, _merchantOptions.CurrentValue.Bank, StringComparison.OrdinalIgnoreCase);

        bool accountMatches = string.Equals(transaction.AccountNumber, invoice.MerchantAccount, StringComparison.Ordinal) &&
                              string.Equals(transaction.AccountNumber, _merchantOptions.CurrentValue.Account, StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(_sePayOptions.CurrentValue.AccountNumber))
        {
            accountMatches = accountMatches && string.Equals(transaction.AccountNumber, _sePayOptions.CurrentValue.AccountNumber, StringComparison.Ordinal);
        }

        if (!bankMatches || !accountMatches)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AccountMismatch);
        }

        bool currencyMatches = string.Equals(invoice.Currency, "VND", StringComparison.OrdinalIgnoreCase);
        bool amountMatches = transaction.TransferAmount == invoice.TotalAmount;
        if (!currencyMatches || !amountMatches)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.AmountMismatch);
        }

        if (!string.IsNullOrWhiteSpace(invoice.PaymentRef) &&
            !string.Equals(invoice.PaymentRef, paymentRef, StringComparison.Ordinal))
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.InvoiceAlreadyPaid);
        }

        if (invoice.Status == InvoiceStatus.Paid)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.InvoiceAlreadyPaid);
        }

        if (bankTimeUtc >= invoice.PaymentDeadline)
        {
            return new(PaymentTransactionStatus.ManualRefundRequired, PaymentTransactionReason.LatePayment);
        }

        if (invoice.Status is InvoiceStatus.Expired or InvoiceStatus.Cancelled)
        {
            return new(PaymentTransactionStatus.ReviewRequired, PaymentTransactionReason.OnTimePaymentInvoiceClosed);

        }
        return new(PaymentTransactionStatus.Matched, PaymentTransactionReason.ExactMatch);
    }

    private static Result ApplyDomainTransition(
        string status,
        Invoice? invoice,
        string paymentRef,
        decimal transferAmount,
        DateTimeOffset bankTimeUtc,
        DateTimeOffset processingTimeUtc,
        out Invoice? invoiceToPersist,
        out OutboxMessage? outbox)
    {
        invoiceToPersist = null;
        outbox = null;

        if (status == PaymentTransactionStatus.ManualRefundRequired && invoice is not null)
        {
            var lateResult = invoice.RecordLatePayment(paymentRef, bankTimeUtc, processingTimeUtc);
            if (lateResult.IsFailure)
            {
                return lateResult;
            }

            outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
                invoice,
                occurredAt: processingTimeUtc,
                paidAt: bankTimeUtc,
                paymentDisposition: InvoicePaidDispositions.LateRequiresRefund);
            invoiceToPersist = invoice;
            return Result.Success();
        }

        if (status == PaymentTransactionStatus.Matched && invoice is not null)
        {
            var markPaidResult = invoice.MarkPaid(paymentRef, transferAmount, "VND", bankTimeUtc);
            if (markPaidResult.IsFailure)
            {
                return markPaidResult;
            }

            outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
                invoice, occurredAt: processingTimeUtc, paidAt: bankTimeUtc);
            invoiceToPersist = invoice;
            return Result.Success();
        }

        return Result.Success();
    }

    private string? ResolveMappedBank(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return null;

        var sePayOptions = _sePayOptions.CurrentValue;
        if (sePayOptions.GatewayMapping.TryGetValue(gateway, out string? mappedBank))
        {
            return mappedBank;
        }

        if (!string.IsNullOrWhiteSpace(sePayOptions.Gateway) &&
            string.Equals(gateway, sePayOptions.Gateway, StringComparison.OrdinalIgnoreCase))
        {
            return _merchantOptions.CurrentValue.Bank;
        }

        if (string.Equals(gateway, _merchantOptions.CurrentValue.Bank, StringComparison.OrdinalIgnoreCase))
        {
            return _merchantOptions.CurrentValue.Bank;
        }

        return null;
    }

    private sealed record MatchingClassification(string Status, string ReasonCode);
}
