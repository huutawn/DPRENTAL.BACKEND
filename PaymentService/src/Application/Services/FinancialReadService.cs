using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.DTOs.Settlement;
using Dprental.PaymentService.Application.IServices;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.Services
{
    public sealed class FinancialReadService(IFinancialReadRepository repository) : IFinancialReadService
    {
        public async Task<Result<InvoiceStatusResponse>> GetInvoiceStatusAsync(
            string invoiceRef, Guid renterId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(invoiceRef) || invoiceRef.Trim().Length > 64 || renterId == Guid.Empty)
                return Error.Validation("Payment.InvoiceRefInvalid", "invoiceRef không hợp lệ.");

            var row = await repository.GetInvoiceStatusAsync(invoiceRef.Trim(), renterId, ct);
            if (row is null) return Error.NotFound("Payment.InvoiceNotFound", "Không tìm thấy hóa đơn.");

            return Result.Success(new InvoiceStatusResponse(
                row.InvoiceRef, row.RentalOrderId, row.Status, row.PaymentDeadline, row.PaidAt,
                row.Items.Sum(x => x.RentalAmount), row.Items.Sum(x => x.DepositAmount), row.Items.Sum(x => x.TotalAmount),
                row.Items.Select(x => new InvoiceStatusItem(
                    x.RentalItemId, x.RentalAmount, x.DepositAmount, x.TotalAmount,
                    x.SettlementStatus?.ToString())).ToList()));
        }

        public async Task<Result<TransactionDetailResponse>> GetTransactionAsync(Guid id, CancellationToken ct)
        {
            if (id == Guid.Empty) return Error.Validation("Payment.TransactionIdInvalid", "id không hợp lệ.");

            var row = await repository.GetTransactionAsync(id, ct);
            if (row is null) return Error.NotFound("Payment.TransactionNotFound", "Không tìm thấy giao dịch.");

            return Result.Success(new TransactionDetailResponse(
                row.Id, row.Provider, row.ProviderTransactionId, row.PaymentRef, row.InvoiceRef,
                row.Amount, row.Status, row.ReasonCode, row.TransactionDate, row.CreatedAt,
                row.ResolvedInvoiceRef, row.ResolvedAt));
        }

        public async Task<Result<SettlementDetailResponse>> GetSettlementAsync(
            Guid id, CallerContext caller, CancellationToken ct)
        {
            if (id == Guid.Empty || caller.UserId == Guid.Empty)
                return Error.Validation("Payment.SettlementIdInvalid", "id không hợp lệ.");

            // Không có quyền và không tồn tại cùng trả 404: không lộ sự tồn tại của settlement.
            var row = await repository.GetSettlementAsync(id, caller.UserId, caller.IsAdmin, ct);
            if (row is null) return Error.NotFound("Payment.SettlementNotFound", "Không tìm thấy settlement.");

            var showRenter = caller.IsAdmin || row.ViewerIsRenter;
            var showSupplier = caller.IsAdmin || row.ViewerIsSupplier;

            return Result.Success(new SettlementDetailResponse(
                row.Id, row.SettlementRef, row.RentalItemId, row.Status.ToString(),
                row.RentalAmount, row.DepositAmount, row.FinalDeductionAmount,
                row.CreatedAt, row.CompletedAt,
                caller.IsAdmin ? row.DecisionRef : null,
                showRenter
                    ? new SettlementRefundView(row.DepositRefundDue, row.RefundRef, row.RefundStatus?.ToString(),
                        row.RefundTransferReference, row.RefundCompletedAt)
                    : null,
                showSupplier
                    ? new SettlementSupplierView(row.SupplierPayable, row.PlatformFeeRate, row.PlatformFeeAmount,
                        row.PayoutRef, row.PayoutStatus, row.PayoutCompletedAt)
                    : null));
        }

        public async Task<Result<FinancialPage<SupplierPayoutListItem>>> ListSupplierPayoutsAsync(
            Guid supplierId, FinancialListQuery q, CancellationToken ct)
        {
            if (supplierId == Guid.Empty) return Error.Validation("Payment.SupplierIdInvalid", "Không xác định được supplier.");
            if (ValidatePaging(q) is { } bad) return bad;
            if (ParsePayoutStatus(q.Status, defaultStatus: null, out var status) is { } badStatus) return badStatus;

            var page = await repository.ListSupplierPayoutsAsync(
                supplierId, new PayoutFilter(status, q.From, q.To), new PageRequest(q.Page, q.PageSize), ct);

            return ToPage(page, q, x => new SupplierPayoutListItem(
                x.PayoutId, x.PayoutRef, x.RentalItemId, x.SettlementRef, x.Amount, x.Currency,
                x.Status, x.ExternalTransferReference, x.CreatedAt, x.CompletedAt));
        }

        public async Task<Result<FinancialPage<AdminPayoutListItem>>> ListAdminPayoutsAsync(
            Guid? supplierId, FinancialListQuery q, CancellationToken ct)
        {
            if (supplierId == Guid.Empty)
                return Error.Validation("Payment.SupplierIdInvalid", "supplierId không hợp lệ.");
            if (ValidatePaging(q) is { } bad) return bad;
            // Hàng đợi: mặc định chỉ lấy payout đang chờ; status=ALL để xem tất cả.
            if (ParsePayoutStatus(q.Status, SupplierPayout.StatusPendingPayout, out var status) is { } badStatus) return badStatus;

            var page = await repository.ListAdminPayoutsAsync(
                supplierId, new PayoutFilter(status, q.From, q.To), new PageRequest(q.Page, q.PageSize), ct);

            return ToPage(page, q, x => new AdminPayoutListItem(
                x.PayoutId, x.PayoutRef, x.SettlementId, x.SettlementRef, x.RentalItemId, x.SupplierId,
                x.Amount, x.Currency, x.Status, x.DestinationRef, x.ExternalTransferReference,
                x.CompletedBy, x.CreatedAt, x.CompletedAt, x.DestinationId, x.DestinationVersion));
        }

        public async Task<Result<FinancialPage<ManualRefundListItem>>> ListManualRefundsAsync(
            FinancialListQuery q, CancellationToken ct)
        {
            if (ValidatePaging(q) is { } bad) return bad;
            if (ParseRefundStatus(q.Status, out var status) is { } badStatus) return badStatus;

            var page = await repository.ListManualRefundsAsync(
                new ManualRefundFilter(status, q.From, q.To), new PageRequest(q.Page, q.PageSize), ct);

            return ToPage(page, q, x => new ManualRefundListItem(
                x.RequirementId, x.RequirementRef, x.SettlementRef, x.RentalItemId, x.RenterId,
                x.Amount, x.Status.ToString(), x.ExternalTransferReference,
                x.CompletedBy, x.CreatedAt, x.CompletedAt));
        }

        public async Task<Result<FinancialPage<FinancialReviewItem>>> ListReviewAsync(
            string? kind, FinancialListQuery q, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(kind) ||
                !Enum.GetNames<FinancialReviewKind>().Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase) ||
                !Enum.TryParse<FinancialReviewKind>(kind, ignoreCase: true, out var parsed))
                return Error.Validation("Payment.ReviewKindInvalid",
                    $"kind phải là một trong: {string.Join(", ", Enum.GetNames<FinancialReviewKind>())}.");
            if (ValidatePaging(q) is { } bad) return bad;
            if (!string.IsNullOrWhiteSpace(q.Status))
                return Error.Validation("Payment.ReviewStatusUnsupported", "Lọc status không áp dụng cho review; hãy chọn kind.");

            var page = await repository.ListReviewAsync(
                parsed, new DateRange(q.From, q.To), new PageRequest(q.Page, q.PageSize), ct);

            return ToPage(page, q, x => new FinancialReviewItem(
                parsed.ToString(), x.Id, x.Reference, x.Status, x.ReasonCode,
                x.Amount, x.RelatedRef, x.CreatedAt));
        }

        private static Result<FinancialPage<TOut>> ToPage<TIn, TOut>(
            ReadPage<TIn> page, FinancialListQuery q, Func<TIn, TOut> map) =>
            Result.Success(new FinancialPage<TOut>(
                page.Items.Select(map).ToList(), q.Page, q.PageSize, page.TotalCount));

        private static Error? ValidatePaging(FinancialListQuery q)
        {
            if (q.Page < 1 || (long)(q.Page - 1) * q.PageSize > int.MaxValue)
                return Error.Validation("Payment.PageInvalid", "page vượt phạm vi hỗ trợ.");
            if (q.PageSize is < 1 or > 100) return Error.Validation("Payment.PageSizeInvalid", "pageSize phải từ 1 đến 100.");
            if (q.From is { } f && q.To is { } t && f >= t)
                return Error.Validation("Payment.DateRangeInvalid", "From phải nhỏ hơn To.");
            return null;
        }

        private static Error? ParsePayoutStatus(string? raw, string? defaultStatus, out string? status)
        {
            status = defaultStatus;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var v = raw.Trim().ToUpperInvariant();
            if (v == "ALL") { status = null; return null; }
            if (v is SupplierPayout.StatusPendingPayout or SupplierPayout.StatusCompleted) { status = v; return null; }
            return Error.Validation("Payment.PayoutStatusInvalid", "status phải là PENDING_PAYOUT, COMPLETED hoặc ALL.");
        }

        private static Error? ParseRefundStatus(string? raw, out ManualRefundStatus? status)
        {
            status = ManualRefundStatus.PendingManualRefund;   // hàng đợi: mặc định chỉ lấy mục đang chờ
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var v = raw.Trim();
            if (v.Equals("ALL", StringComparison.OrdinalIgnoreCase)) { status = null; return null; }
            if (Enum.TryParse<ManualRefundStatus>(v, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            { status = parsed; return null; }
            return Error.Validation("Payment.RefundStatusInvalid", "status phải là PendingManualRefund, Completed hoặc ALL.");
        }
    }
}
