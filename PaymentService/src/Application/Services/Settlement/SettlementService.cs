using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;

namespace Dprental.PaymentService.Application.Services;

public sealed class SettlementService : ISettlementService
{
    private readonly IInvoiceRepository _invoices;
    private readonly ISettlementLedger _ledger;
    private readonly ITransactionIntegrityChecker _integrityChecker;

    public SettlementService(
        IInvoiceRepository invoices,
        ISettlementLedger ledger,
        ITransactionIntegrityChecker integrityChecker)
    {
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _integrityChecker = integrityChecker ?? throw new ArgumentNullException(nameof(integrityChecker));
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

        var verifyRes = await _integrityChecker.VerifyAsync(new Dprental.PaymentService.Application.DTOs.Verification.TransactionVerificationRequest(
            PaymentRef: request.PaymentRef.Trim(),
            Purpose: Dprental.PaymentService.Application.DTOs.Verification.VerificationPurposes.Settlement,
            CallerNonce: Guid.NewGuid().ToString("N"),
            CallerIdentity: "settlement-service",
            ExpectedAmount: invoice.TotalAmount,
            ExpectedCurrency: invoice.Currency,
            InvoiceRef: invoice.InvoiceRef,
            ExpectedOrderId: invoice.RentalOrderId,
            RentalItemId: request.RentalItemId,
            SupplierId: request.SupplierId), ct);

        if (verifyRes.IsFailure)
        {
            return verifyRes.Error;
        }

        if (!verifyRes.Value.Usability.IsUsable)
        {
            return Error.Conflict("Payment.SettlementIntegrityFailed",
                $"Giao dịch thanh toán chưa được xác minh toàn vẹn hoặc không đủ điều kiện settle: {verifyRes.Value.Usability.Reason}");
        }

        if (DateTimeOffset.UtcNow > verifyRes.Value.Receipt!.ExpiresAt)
        {
            return Error.Conflict("Payment.ReceiptExpired", "Verification receipt đã hết hạn trước khi bắt đầu transaction ghi sổ.");
        }

        var claims = (verifyRes.Value.Receipt!.Body.EntityClaims ?? [])
            .Select(c => new Dprental.PaymentService.Application.DTOs.Verification.VerifiedWriteClaim(c.EntityType, c.EntityId, c.Revision, c.Digest))
            .ToList();

        var invClaim = claims.FirstOrDefault(c => c.EntityType == "Invoice" && c.EntityId == invoice.InvoiceRef);
        if (invClaim is null || invClaim.Revision != invoice.Revision)
        {
            return Error.Conflict("Payment.SettlementClaimMismatch", "Không tìm thấy claim hóa đơn đã xác thực hoặc revision không khớp.");
        }

        var itemClaim = claims.FirstOrDefault(c => c.EntityType == "InvoiceItem" && c.EntityId == item.RentalItemId.ToString("D"));
        if (itemClaim is null || itemClaim.Revision != item.Revision)
        {
            return Error.Conflict("Payment.SettlementClaimMismatch", "Không tìm thấy claim item đã xác thực hoặc revision không khớp.");
        }

        var verifiedContext = new Dprental.PaymentService.Application.DTOs.Verification.VerifiedWriteContext(
            verifyRes.Value.Receipt.ReceiptId,
            verifyRes.Value.Receipt.KeyId,
            verifyRes.Value.Receipt.ExpiresAt,
            claims,
            DateTimeOffset.UtcNow);

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