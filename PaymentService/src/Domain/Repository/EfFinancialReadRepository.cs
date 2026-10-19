using System.Linq.Expressions;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repository;

public sealed class EfFinancialReadRepository(PaymentServiceDbContext db) : IFinancialReadRepository
{

    public async Task<InvoiceStatusRow?> GetInvoiceStatusAsync(string invoiceRef, Guid renterId, CancellationToken ct)
    {
        // Quyền (renter sở hữu) nằm ngay trong câu truy vấn.
        var invoice = await db.Invoices.AsNoTracking()
            .Where(i => i.InvoiceRef == invoiceRef && i.RenterId == renterId)
            .Select(i => new { i.Id, i.InvoiceRef, i.RentalOrderId, i.Status, i.PaymentDeadline, i.PaidAt })
            .SingleOrDefaultAsync(ct);
        if (invoice is null) return null;

        var items = await db.InvoiceItems.AsNoTracking()
            .Where(ii => ii.InvoiceId == invoice.Id)
            .OrderBy(ii => ii.RentalItemId)
            .Select(ii => new InvoiceStatusItemRow(
                ii.RentalItemId, ii.RentalAmount, ii.DepositAmount, ii.TotalAmount,
                db.RentalItemSettlements.Where(s => s.InvoiceItemId == ii.Id)
                    .Select(s => (SettlementState?)s.Status).FirstOrDefault()))
            .ToListAsync(ct);

        return new InvoiceStatusRow(invoice.InvoiceRef, invoice.RentalOrderId, invoice.Status,
            invoice.PaymentDeadline, invoice.PaidAt, items);
    }

    public Task<TransactionDetailRow?> GetTransactionAsync(Guid id, CancellationToken ct) =>
        db.PaymentTransactions.AsNoTracking()
            .Where(t => t.Id == id)
            // Danh sách trắng: tuyệt đối không chiếu dữ liệu webhook gốc, số tài khoản hay secret ra ngoài.
            .Select(t => new TransactionDetailRow(
                t.Id, t.Provider, t.ProviderTransactionId, t.PaymentRef, t.InvoiceRef,
                t.TransferAmount, t.Status, t.ReasonCode, t.TransactionDate, t.CreatedAt,
                db.PaymentTransactionResolutions.Where(r => r.TransactionId == t.Id)
                    .Select(r => r.InvoiceRef).FirstOrDefault(),
                db.PaymentTransactionResolutions.Where(r => r.TransactionId == t.Id)
                    .Select(r => (DateTimeOffset?)r.OccurredAt).FirstOrDefault()))
            .SingleOrDefaultAsync(ct);

    public Task<SettlementDetailRow?> GetSettlementAsync(Guid id, Guid callerId, bool isAdmin, CancellationToken ct) =>
        (from s in db.RentalItemSettlements.AsNoTracking()
         where s.Id == id
         join i in db.Invoices.AsNoTracking() on s.InvoiceId equals i.Id
         where isAdmin || s.SupplierId == callerId || i.RenterId == callerId   // quyền trong SQL
         select new SettlementDetailRow(
             s.Id, s.SettlementRef, s.RentalItemId, s.Status,
             s.RentalAmount, s.DepositAmount, s.FinalDeductionAmount, s.DepositRefundDue,
             s.SupplierPayable, s.PlatformFeeRate, s.PlatformFeeAmount, s.DecisionRef,
             s.CreatedAt, s.CompletedAt,
             s.RefundRequirement == null ? null : s.RefundRequirement.RequirementRef,
             s.RefundRequirement == null ? (ManualRefundStatus?)null : s.RefundRequirement.Status,
             s.RefundRequirement == null ? null : s.RefundRequirement.ExternalTransferReference,
             s.RefundRequirement == null ? null : s.RefundRequirement.CompletedAt,
             s.Payout == null ? null : s.Payout.PayoutRef,
             s.Payout == null ? null : s.Payout.Status,
             s.Payout == null ? null : s.Payout.CompletedAt,
             i.RenterId == callerId,
             s.SupplierId == callerId))
        .SingleOrDefaultAsync(ct);

    public async Task<ReadPage<SupplierPayoutRow>> ListSupplierPayoutsAsync(
        Guid supplierId, PayoutFilter f, PageRequest p, CancellationToken ct)
    {
        var q = FilterPayouts(db.SupplierPayouts.AsNoTracking().Where(x => x.SupplierId == supplierId), f);

        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(p.Skip).Take(p.PageSize)
            .Select(x => new SupplierPayoutRow(
                x.Id, x.PayoutRef, x.RentalItemId, x.Settlement!.SettlementRef,
                x.Amount, x.Currency, x.Status, x.ExternalTransferReference,
                x.CreatedAt, x.CompletedAt))                       // không có DestinationRef
            .ToListAsync(ct);

        return new ReadPage<SupplierPayoutRow>(items, total);
    }

    public async Task<ReadPage<AdminPayoutRow>> ListAdminPayoutsAsync(
        Guid? supplierId, PayoutFilter f, PageRequest p, CancellationToken ct)
    {
        var q = FilterPayouts(db.SupplierPayouts.AsNoTracking(), f);
        if (supplierId is { } sid) q = q.Where(x => x.SupplierId == sid);

        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)   // hàng đợi: cũ nhất trước
            .Skip(p.Skip).Take(p.PageSize)
            .Select(x => new AdminPayoutRow(
                x.Id, x.PayoutRef, x.SettlementId, x.Settlement!.SettlementRef, x.RentalItemId, x.SupplierId,
                x.Amount, x.Currency, x.Status, x.DestinationRef, x.ExternalTransferReference,
                x.CompletedBy, x.CreatedAt, x.CompletedAt,
                x.DestinationId, x.Destination == null ? (int?)null : x.Destination.Version))
            .ToListAsync(ct);

        return new ReadPage<AdminPayoutRow>(items, total);
    }

    public async Task<ReadPage<ManualRefundRow>> ListManualRefundsAsync(
        ManualRefundFilter f, PageRequest p, CancellationToken ct)
    {
        var status = f.Status; var from = f.From; var to = f.To;
        var q = db.ManualRefundRequirements.AsNoTracking().Where(x =>
            (status == null || x.Status == status) &&
            (from == null || x.CreatedAt >= from) &&
            (to == null || x.CreatedAt < to));

        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip(p.Skip).Take(p.PageSize)
            .Select(x => new ManualRefundRow(
                x.Id, x.RequirementRef, x.Settlement!.SettlementRef, x.RentalItemId, x.RenterId,
                x.Amount, x.Status, x.ExternalTransferReference,
                x.CompletedBy, x.CreatedAt, x.CompletedAt))
            .ToListAsync(ct);

        return new ReadPage<ManualRefundRow>(items, total);
    }

    public Task<ReadPage<ReviewRow>> ListReviewAsync(
        FinancialReviewKind kind, DateRange r, PageRequest p, CancellationToken ct)
    {
        var from = r.From; var to = r.To;

        switch (kind)
        {
            case FinancialReviewKind.AwaitingPayment:
                return ReviewPageAsync(
                    db.Invoices.AsNoTracking().Where(i =>
                        i.Status == InvoiceStatus.Pending &&
                        (from == null || i.CreatedAt >= from) && (to == null || i.CreatedAt < to)),
                    i => new ReviewRow(i.Id, i.InvoiceRef, i.Status, null,
                        db.InvoiceItems.Where(ii => ii.InvoiceId == i.Id).Sum(ii => ii.TotalAmount),
                        i.PaymentCode, i.CreatedAt),
                    i => i.CreatedAt, i => i.Id, p, ct);

            case FinancialReviewKind.UnmatchedTransaction:
                return TransactionReviewAsync(PaymentTransactionStatus.Unmatched, null, from, to, p, ct);
            case FinancialReviewKind.LateTransaction:
                return TransactionReviewAsync(PaymentTransactionStatus.ManualRefundRequired,
                    PaymentTransactionReason.LatePayment, from, to, p, ct);
            case FinancialReviewKind.ReviewRequiredTransaction:
                return TransactionReviewAsync(PaymentTransactionStatus.ReviewRequired, null, from, to, p, ct);

            case FinancialReviewKind.SettlementPendingClose:
                return ReviewPageAsync(
                    db.RentalItemSettlements.AsNoTracking().Where(s =>
                        s.Status == SettlementState.PendingManualRefund &&
                        (from == null || s.CreatedAt >= from) && (to == null || s.CreatedAt < to)),
                    s => new ReviewRow(s.Id, s.SettlementRef, nameof(SettlementState.PendingManualRefund), null,
                        s.DepositRefundDue,
                        s.RefundRequirement == null ? null : s.RefundRequirement.RequirementRef,
                        s.CreatedAt),
                    s => s.CreatedAt, s => s.Id, p, ct);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private Task<ReadPage<ReviewRow>> TransactionReviewAsync(
        string status, string? reason, DateTimeOffset? from, DateTimeOffset? to, PageRequest p, CancellationToken ct) =>
        ReviewPageAsync(
            db.PaymentTransactions.AsNoTracking().Where(t =>
                t.Status == status && (reason == null || t.ReasonCode == reason) &&
                (status != PaymentTransactionStatus.Unmatched ||
                 !db.PaymentTransactionResolutions.Any(resolution => resolution.TransactionId == t.Id)) &&
                (from == null || t.CreatedAt >= from) && (to == null || t.CreatedAt < to)),
            t => new ReviewRow(t.Id, t.ProviderTransactionId, t.Status, t.ReasonCode,
                t.TransferAmount, t.InvoiceRef, t.CreatedAt),
            t => t.CreatedAt, t => t.Id, p, ct);

    // Lọc và đếm trong SQL trước, sau đó mới chiếu và phân trang: tổng số luôn khớp với trang.
    private static async Task<ReadPage<ReviewRow>> ReviewPageAsync<TSource>(
        IQueryable<TSource> filtered, Expression<Func<TSource, ReviewRow>> select,
        Expression<Func<TSource, DateTimeOffset>> createdAt, Expression<Func<TSource, Guid>> id,
        PageRequest p, CancellationToken ct)
    {
        var total = await filtered.LongCountAsync(ct);
        var items = await filtered
            .OrderBy(createdAt).ThenBy(id)
            .Skip(p.Skip).Take(p.PageSize)
            .Select(select)
            .ToListAsync(ct);
        return new ReadPage<ReviewRow>(items, total);
    }

    private static IQueryable<SupplierPayout> FilterPayouts(IQueryable<SupplierPayout> q, PayoutFilter f)
    {
        var status = f.Status; var from = f.From; var to = f.To;
        return q.Where(x =>
            (status == null || x.Status == status) &&
            (from == null || x.CreatedAt >= from) &&
            (to == null || x.CreatedAt < to));
    }
}
