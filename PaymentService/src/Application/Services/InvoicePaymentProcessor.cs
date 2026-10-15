using System.Globalization;
using Dprental.PaymentService.Application.DTOs.Bank;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Services;

public sealed class InvoicePaymentProcessor : IInvoicePaymentProcessor
{
    private const string ProviderName = "SEPAY";

    private readonly IInvoiceRepository _repository;
    private readonly IOptionsMonitor<PaymentMerchantOptions> _merchantOptions;
    private readonly IOptionsMonitor<SePayOptions> _sePayOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InvoicePaymentProcessor> _logger;

    public InvoicePaymentProcessor(
        IInvoiceRepository repository,
        IOptionsMonitor<PaymentMerchantOptions> merchantOptions,
        IOptionsMonitor<SePayOptions> sePayOptions,
        TimeProvider? timeProvider = null,
        ILogger<InvoicePaymentProcessor>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _merchantOptions = merchantOptions ?? throw new ArgumentNullException(nameof(merchantOptions));
        _sePayOptions = sePayOptions ?? throw new ArgumentNullException(nameof(sePayOptions));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<InvoicePaymentProcessor>.Instance;
    }

    public async Task<Result> ProcessAsync(VerifiedBankTransaction transaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        string providerTxId = transaction.Id.ToString(CultureInfo.InvariantCulture);
        string paymentRef = $"{ProviderName}:{transaction.Id}";
        DateTimeOffset processingTimeUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        DateTimeOffset bankTimeUtc = transaction.TransactionDate;

        await using var tx = await _repository.BeginTransactionAsync(ct);
        try
        {
            // 1. Claim Provider ID
            var existingTx = await _repository.FindPaymentTransactionWithRowLockAsync(ProviderName, providerTxId, ct);
            if (existingTx is not null)
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

                await tx.RollbackAsync(ct);

                if (matches)
                {
                    return Result.Success();
                }

                return Error.Conflict(
                    "DUPLICATE_PROVIDER_TRANSACTION",
                    "Giao dịch trùng ID nhà cung cấp nhưng dữ liệu nghiệp vụ không khớp.");
            }

            // 2. Extract PaymentCode
            var extraction = PaymentCodeExtractor.Extract(transaction.Code, transaction.Content);

            string? paymentCode = null;
            Invoice? invoice = null;
            Invoice? invoiceToPersist = null;
            OutboxMessage? outbox = null;
            string status;
            string reasonCode;

            if (extraction.Status == CodeExtractionStatus.Missing)
            {
                status = PaymentTransactionStatus.Unmatched;
                reasonCode = PaymentTransactionReason.MissingPaymentCode;
            }
            else if (extraction.Status == CodeExtractionStatus.Ambiguous)
            {
                status = PaymentTransactionStatus.ReviewRequired;
                reasonCode = PaymentTransactionReason.AmbiguousPaymentCode;
            }
            else
            {
                paymentCode = extraction.Code!;

                // 3. Lock Invoice row by PaymentCode
                invoice = await _repository.FindInvoiceByPaymentCodeWithRowLockAsync(paymentCode, ct);
                if (invoice is null)
                {
                    status = PaymentTransactionStatus.Unmatched;
                    reasonCode = PaymentTransactionReason.InvoiceNotFound;
                }
                else
                {
                    // Refresh processing timestamp after acquiring row lock to evaluate late payment accurately
                    processingTimeUtc = _timeProvider.GetUtcNow().ToUniversalTime();

                    // 4. Validate incoming transfer
                    bool isIncoming = string.Equals(transaction.TransferType, "in", StringComparison.OrdinalIgnoreCase) && transaction.TransferAmount > 0;
                    if (!isIncoming)
                    {
                        status = PaymentTransactionStatus.ReviewRequired;
                        reasonCode = PaymentTransactionReason.OutgoingTransfer;
                    }
                    else
                    {
                        // 5. Validate receiving account and bank gateway mapping
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
                            status = PaymentTransactionStatus.ReviewRequired;
                            reasonCode = PaymentTransactionReason.AccountMismatch;
                        }
                        else
                        {
                            // 6. Validate currency and amount
                            bool currencyMatches = string.Equals(invoice.Currency, "VND", StringComparison.OrdinalIgnoreCase);
                            bool amountMatches = transaction.TransferAmount == invoice.TotalAmount;
                            if (!currencyMatches || !amountMatches)
                            {
                                status = PaymentTransactionStatus.ReviewRequired;
                                reasonCode = PaymentTransactionReason.AmountMismatch;
                            }
                            else
                            {
                                // 7. Check if invoice already recorded another payment
                                if (!string.IsNullOrWhiteSpace(invoice.PaymentRef) &&
                                    !string.Equals(invoice.PaymentRef, paymentRef, StringComparison.Ordinal))
                                {
                                    status = PaymentTransactionStatus.ReviewRequired;
                                    reasonCode = PaymentTransactionReason.InvoiceAlreadyPaid;
                                }
                                else if (invoice.Status == InvoiceStatus.Paid)
                                {
                                    status = PaymentTransactionStatus.ReviewRequired;
                                    reasonCode = PaymentTransactionReason.InvoiceAlreadyPaid;
                                }
                                else
                                {
                                    // 8. Late Payment branching
                                    bool isLate = bankTimeUtc >= invoice.PaymentDeadline ||
                                                  processingTimeUtc >= invoice.PaymentDeadline ||
                                                  invoice.Status is InvoiceStatus.Expired or InvoiceStatus.Cancelled;

                                    if (isLate)
                                    {
                                        var lateResult = invoice.RecordLatePayment(paymentRef, bankTimeUtc, processingTimeUtc);
                                        if (lateResult.IsFailure)
                                        {
                                            await tx.RollbackAsync(ct);
                                            return lateResult;
                                        }

                                        outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
                                            invoice, occurredAt: processingTimeUtc, paidAt: bankTimeUtc);
                                        invoiceToPersist = invoice;
                                        status = PaymentTransactionStatus.ManualRefundRequired;
                                        reasonCode = PaymentTransactionReason.LatePayment;
                                    }
                                    else
                                    {
                                        // 9. Fully matched, on time, Invoice is PENDING
                                        var markPaidResult = invoice.MarkPaid(paymentRef, transaction.TransferAmount, "VND", bankTimeUtc);
                                        if (markPaidResult.IsFailure)
                                        {
                                            await tx.RollbackAsync(ct);
                                            return markPaidResult;
                                        }

                                        outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(
                                            invoice, occurredAt: processingTimeUtc, paidAt: bankTimeUtc);
                                        invoiceToPersist = invoice;
                                        status = PaymentTransactionStatus.Matched;
                                        reasonCode = PaymentTransactionReason.ExactMatch;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Single unified persistence point for all statuses, ensuring ProviderCode is always stored
            var pt = PaymentTransaction.Create(
                ProviderName, providerTxId, paymentRef, transaction.Gateway, transaction.AccountNumber,
                transaction.SubAccount, transaction.TransferType, transaction.TransferAmount, transaction.Accumulated,
                bankTimeUtc, transaction.ReferenceCode, transaction.Description, transaction.Content,
                providerCode: transaction.Code,
                paymentCode: paymentCode,
                invoiceId: invoice?.Id,
                invoiceRef: invoice?.InvoiceRef,
                status: status,
                reasonCode: reasonCode,
                createdAt: processingTimeUtc);

            await _repository.SavePaymentTransactionAsync(pt, invoiceToPersist, outbox, ct);
            await tx.CommitAsync(ct);
            return Result.Success();
        }
        catch (StorageConstraintViolationException ex)
        {
            await tx.RollbackAsync(ct);

            if (ex.ViolationType == StorageConstraintViolationType.PaymentTransaction)
            {
                var reloaded = await _repository.FindPaymentTransactionWithRowLockAsync(ProviderName, providerTxId, ct);
                if (reloaded is not null && reloaded.MatchesBusinessData(
                    transaction.Gateway, transaction.AccountNumber, transaction.TransferType,
                    transaction.TransferAmount, transaction.TransactionDate, transaction.ReferenceCode,
                    transaction.Content, transaction.Code))
                {
                    return Result.Success();
                }

                return Error.Conflict("DUPLICATE_PROVIDER_TRANSACTION", "Giao dịch trùng ID nhà cung cấp nhưng dữ liệu nghiệp vụ không khớp.");
            }

            if (ex.ViolationType == StorageConstraintViolationType.PaymentRef)
            {
                return Error.Conflict("DUPLICATE_PAYMENT_REF", "PaymentRef đã tồn tại trên một hóa đơn khác.");
            }

            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
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
}
