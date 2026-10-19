using Dprental.PaymentService.Application.DTOs.Operations;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Application.DTOs.Invoice;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.DTOs.Settlement;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dprental.PaymentService.Application.IServices
{
    public sealed record CallerContext(Guid UserId, bool IsAdmin);

    public interface IFinancialReadService
    {
        Task<Result<InvoiceStatusResponse>> GetInvoiceStatusAsync(string invoiceRef, Guid renterId, CancellationToken ct);
        Task<Result<TransactionDetailResponse>> GetTransactionAsync(Guid id, CancellationToken ct);
        Task<Result<SettlementDetailResponse>> GetSettlementAsync(Guid id, CallerContext caller, CancellationToken ct);
    Task<Result<FinancialPage<SupplierPayoutListItem>>> ListSupplierPayoutsAsync(Guid supplierId, FinancialListQuery q, CancellationToken ct);
    Task<Result<FinancialPage<AdminPayoutListItem>>> ListAdminPayoutsAsync(Guid? supplierId, FinancialListQuery q, CancellationToken ct);
    Task<Result<FinancialPage<ManualRefundListItem>>> ListManualRefundsAsync(FinancialListQuery q, CancellationToken ct);
    Task<Result<FinancialPage<FinancialReviewItem>>> ListReviewAsync(string? kind, FinancialListQuery q, CancellationToken ct);
    }
}
