using System.Globalization;
using Dprental.PaymentService.Application.DTOs.SePay;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoicePaymentProcessor : IInvoicePaymentProcessor
{
    private const string ProviderName = "SEPAY";

    private readonly IInvoiceRepository _repository;
    private readonly IOptionsMonitor<PaymentMerchantOptions> _merchantOptions;
    private readonly IOptionsMonitor<SePayOptions> _sePayOptions;
    private readonly TimeProvider _timeProvider;

    public InvoicePaymentProcessor(
        IInvoiceRepository repository,
        IOptionsMonitor<PaymentMerchantOptions> merchantOptions,
        IOptionsMonitor<SePayOptions> sePayOptions,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _sePayOptions = sePayOptions ?? throw new ArgumentNullException(nameof(sePayOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result> ProcessAsync(SePayWebhookPayload? payload, CancellationToken cancellationToken = default)
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

            // 3. Extract code and acquire Invoice row lock when single code found
            var (extraction, invoice) = await ResolveInvoiceAsync(transaction, cancellationToken);

            // 4. Obtain processing time after lock and evaluate matching classification
            DateTimeOffset processingTimeUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            DateTimeOffset bankTimeUtc = transaction.TransactionDate;

            var classification = ClassifyTransaction(
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

            // 7. Persist atomically and commit
            await _repository.SavePaymentTransactionAsync(pt, invoiceToPersist, outbox, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return Result.Success();
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

    private static Result EvaluateDuplicateTransaction(PaymentTransaction existingTx, NormalizedBankTransaction transaction)
    {
        bool matches = existingTx.MatchesBusinessData(
            transaction.Gateway,
            transaction.AccountNumber,
            transaction.TransferType,
            transaction.TransferAmount,
            transaction.TransactionDate,
            transaction.ReferenceCode,
            transaction.Content,
            transaction.Code);

        if (matches)
        {
            return Result.Success();
        }

        return Error.Conflict(
            "DUPLICATE_PROVIDER_TRANSACTION",
            "Giao dịch trùng ID nhà cung cấp nhưng dữ liệu nghiệp vụ không khớp.");
    }

    private async Task<(CodeExtractionResult Extraction, Invoice? Invoice)> ResolveInvoiceAsync(
        NormalizedBankTransaction transaction,
        CancellationToken ct)
    {
        var extraction = PaymentCodeExtractor.Extract(transaction.Code, transaction.Content);
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

        bool isLate = bankTimeUtc >= invoice.PaymentDeadline ||
                      processingTimeUtc >= invoice.PaymentDeadline ||
                      invoice.Status is InvoiceStatus.Expired or InvoiceStatus.Cancelled;

        if (isLate)
        {
            return new(PaymentTransactionStatus.ManualRefundRequired, PaymentTransactionReason.LatePayment);
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
                invoice, occurredAt: processingTimeUtc, paidAt: bankTimeUtc);
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
