using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Repository;

namespace Dprental.PaymentService.Application.Services;

public sealed class PaymentRecordingService : IPaymentRecordingService
{
    private readonly IInvoiceRepository _repository;
    private readonly TimeProvider _timeProvider;

    public PaymentRecordingService(
        IInvoiceRepository repository,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Result> RecordPaymentReceivedAsync(
        string invoiceRef,
        string paymentRef,
        decimal amount,
        string currency,
        DateTimeOffset paidAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceRef) || string.IsNullOrWhiteSpace(paymentRef))
        {
            return Error.Validation("Payment.InvalidInput", "InvoiceRef và PaymentRef không được rỗng.");
        }

        var amountCheck = VndAmountValidator.ValidateVndAmount(amount, "Số tiền thanh toán");
        if (amountCheck.IsFailure)
        {
            return amountCheck.Error;
        }

        string normalizedInvoiceRef = invoiceRef.Trim();
        string normalizedPaymentRef = paymentRef.Trim();
        DateTimeOffset utcPaidAt = paidAt.ToUniversalTime();

        await using var tx = await _repository.BeginTransactionAsync(ct);
        try
        {
            var invoice = await _repository.FindInvoiceWithRowLockAsync(normalizedInvoiceRef, ct);
            if (invoice is null)
            {
                await tx.RollbackAsync(ct);
                return Error.NotFound("INVOICE_NOT_FOUND", "Không tìm thấy hóa đơn.");
            }

            // 1. Keep PAID replay check before any time/deadline validation
            if (invoice.Status == InvoiceStatus.Paid)
            {
                var replayResult = invoice.MarkPaid(normalizedPaymentRef, amount, currency, utcPaidAt);
                await tx.RollbackAsync(ct);
                return replayResult;
            }

            // 2. Check duplicate PaymentRef on other invoices
            bool duplicatePaymentRef = await _repository.IsPaymentRefDuplicateAsync(
                normalizedPaymentRef, normalizedInvoiceRef, ct);

            if (duplicatePaymentRef)
            {
                await tx.RollbackAsync(ct);
                return Error.Conflict("DUPLICATE_PAYMENT_REF", "PaymentRef đã tồn tại trên một hóa đơn khác.");
            }

            // 3. If invoice is PENDING and current time is already at/past deadline, persist EXPIRED and do not emit InvoicePaid
            DateTimeOffset utcNow = _timeProvider.GetUtcNow().ToUniversalTime();
            if (invoice.Status == InvoiceStatus.Pending && utcNow >= invoice.PaymentDeadline)
            {
                invoice.Expire(utcNow);
                await _repository.SaveInvoiceChangesAsync(invoice, null, null, ct);
                await tx.CommitAsync(ct);
                return Error.Conflict("INVOICE_EXPIRED", "Hóa đơn đã hết hạn, không thể thanh toán.");
            }

            bool wasPending = invoice.Status == InvoiceStatus.Pending;

            var markResult = invoice.MarkPaid(normalizedPaymentRef, amount, currency, utcPaidAt);
            if (markResult.IsFailure)
            {
                await tx.RollbackAsync(ct);
                return markResult;
            }

            OutboxMessage? outbox = null;
            if (wasPending)
            {
                outbox = PaymentOutboxFactory.CreateInvoicePaidOutboxMessage(invoice, utcPaidAt);
            }

            await _repository.SaveInvoiceChangesAsync(invoice, null, outbox, ct);
            await tx.CommitAsync(ct);

            return Result.Success();
        }
        catch (StorageConstraintViolationException ex)
        {
            await tx.RollbackAsync(ct);

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
}
