using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;

namespace Dprental.PaymentService.Application.Services;

public sealed class SettlementService : ISettlementService
{
    private readonly IInvoiceRepository _invoices;
    private readonly ISettlementLedger _ledger;
    private readonly ITransactionIntegrityChecker? _integrityChecker;

    public SettlementService(
        IInvoiceRepository invoices,
        ISettlementLedger ledger,
        ITransactionIntegrityChecker? integrityChecker = null)
    {
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _integrityChecker = integrityChecker;
    }

    public async Task<Result<SettleRentalResponse>> SettleAsync(SettleRentalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 400: dữ liệu sai (đã gồm Refunded == Deposit - Deduction).
        var validation = request.Validate();
        if (validation.IsFailure) return validation.Error;

        // 404: không có hóa đơn hoặc item.
        var invoice = await _invoices.GetInvoiceSnapshotAsync(request.InvoiceRef.Trim(), ct);
        if (invoice is null)
        {
            return Error.NotFound("Payment.InvoiceNotFound", "Không tìm thấy hóa đơn.");
        }

        var item = invoice.Items.SingleOrDefault(i => i.RentalItemId == request.RentalItemId);
        if (item is null)
        {
            return Error.NotFound("Payment.InvoiceItemNotFound", "RentalItem không thuộc hóa đơn này.");
        }

        // 409: trạng thái thanh toán và snapshot không khớp.
        if (invoice.RentalOrderId != request.RentalOrderId)
            return Error.Conflict("Payment.RentalOrderMismatch", "RentalOrderId không khớp hóa đơn.");

        // PaymentRef có thể đã được gán cho hóa đơn EXPIRED (thanh toán trễ), nên phải kiểm tra Status.
        if (!string.Equals(invoice.Status, InvoiceStatus.Paid, StringComparison.Ordinal))
            return Error.Conflict("Payment.InvoiceNotPaid", "Hóa đơn chưa được thanh toán nên chưa thể settle.");

        if (_integrityChecker is not null)
        {
            var verifyRes = await _integrityChecker.VerifyAsync(new Dprental.PaymentService.Application.DTOs.Verification.TransactionVerificationRequest(
                PaymentRef: request.PaymentRef.Trim(),
                Purpose: Dprental.PaymentService.Application.DTOs.Verification.VerificationPurposes.Settlement,
                CallerNonce: Guid.NewGuid().ToString("N"),
                ExpectedAmount: invoice.TotalAmount,
                ExpectedCurrency: invoice.Currency,
                ExpectedStatus: InvoiceStatus.Paid,
                InvoiceRef: invoice.InvoiceRef,
                ExpectedOrderId: invoice.RentalOrderId,
                RentalItemId: request.RentalItemId,
                SupplierId: request.SupplierId), ct);

            if (verifyRes.IsFailure || !verifyRes.Value.Usability.IsUsable)
            {
                return Error.Conflict("Payment.SettlementIntegrityFailed",
                    $"Giao dịch thanh toán chưa được xác minh toàn vẹn hoặc không đủ điều kiện settle: {verifyRes.Value?.Usability.Reason ?? verifyRes.Error.Message}");
            }
        }

        if (!string.Equals(invoice.PaymentRef, request.PaymentRef.Trim(), StringComparison.Ordinal))
            return Error.Conflict("Payment.PaymentRefMismatch", "PaymentRef không khớp hóa đơn.");

        if (!string.Equals(invoice.Currency, request.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
            return Error.Conflict("Payment.CurrencyMismatch", "Currency không khớp hóa đơn.");

        if (item.SupplierId != request.SupplierId)
            return Error.Conflict("Payment.SupplierMismatch", "SupplierId không khớp snapshot hóa đơn.");

        if (item.DepositAmount != request.DepositAmount)
            return Error.Conflict("Payment.DepositMismatch", "Tiền cọc không khớp snapshot hóa đơn.");

        // Tiền thuê lấy từ snapshot Payment, không lấy từ request.
        var calc = RentalItemSettlement.Create(
            item, request.FinalDeductionAmount, request.DecisionRef, request.OperationKey);
        if (calc.IsFailure) return calc.Error;

        var settlement = calc.Value;

        var written = await _ledger.RecordSettlementAsync(settlement, ct);
        if (written.IsFailure) return written.Error;

        var w = written.Value;

        // COMPLETED chỉ khi không còn khoản cọc nào đang chờ chuyển khoản.
        var status = w.State == SettlementState.Completed
            ? SettlementStatus.Completed
            : SettlementStatus.PendingManualRefund;

        return Result.Success(new SettleRentalResponse(
            w.SettlementRef,
            settlement.OperationKey,
            status,
            settlement.FinalDeductionAmount,
            settlement.DepositRefundDue,
            invoice.Currency,
            w.Outcome == SettlementWriteOutcome.Replayed));
    }
}