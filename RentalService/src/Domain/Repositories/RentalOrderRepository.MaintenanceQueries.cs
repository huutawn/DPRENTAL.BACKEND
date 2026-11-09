using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed partial class RentalOrderRepository
{
    public async Task<IReadOnlyList<Guid>> GetPendingPaymentExpiredOrderIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.Status == RentalOrderStatus.PendingPayment && o.PaymentDeadline <= utcNow)
            .OrderBy(o => o.PaymentDeadline)
            .Select(o => o.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingSupplierTimeoutItemIdsAsync(DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSupplierConfirmation &&
                        i.SupplierConfirmationDeadline.HasValue &&
                        i.SupplierConfirmationDeadline.Value <= utcNow)
            .OrderBy(i => i.SupplierConfirmationDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }


    public async Task<IReadOnlyList<Guid>> GetOverdueUnprotectedOrderIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        DateOnly todayVn = RentalItem.TodayInVietnam(utcNow);

        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.InRental
                     && i.ExpectedEndDate < todayVn
                     && i.UnitAssignments.Any(u =>
                            u.Status == RentalUnitAssignmentStatus.HandedOver
                            // Chỉ receipt "exhausted" mới không chặn; Succeeded/đang chạy/Business đều chặn.
                            && !i.RentalOrder!.OperationReceipts.Any(r =>
                                   r.Action == RentalItem.OverdueProtectionAction
                                && EF.Functions.Like(r.OperationId, "overdue:" + u.Id.ToString() + ":%")
                                && !(r.Status == RentalOperationReceipt.StatusFailed
                                     && (r.ErrorClassification == RentalOperationReceipt.ClassificationTransientExhausted
                                      || r.ErrorClassification == RentalOperationReceipt.ClassificationUncertainExhausted)))))
            .GroupBy(i => i.RentalOrderId)
            .OrderBy(g => g.Min(i => i.ExpectedEndDate))
            .Select(g => g.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetHandoverOverdueOrderIdsAsync(
        DateOnly todayInVietnam, DateOnly autoExpireStartBefore, int batchSize,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.AwaitingHandover
                     && i.ExpectedStartDate < autoExpireStartBefore)
            .GroupBy(i => i.RentalOrderId)
            .OrderBy(g => g.Min(i => i.ExpectedStartDate))
            .Select(g => g.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetReturnConfirmationOverdueOrderIdsAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ReturnRequests
            .AsNoTracking()
            .Where(r => r.ConfirmationDeadlineAt <= now
                     && r.Units.Any(u => u.ClosedAt == null && u.RejectedAt == null && u.OverdueNotifiedAt == null))
            .Join(_dbContext.RentalItems, r => r.RentalItemId, i => i.Id, (r, i) => new { r, i.RentalOrderId })
            .GroupBy(x => x.RentalOrderId)
            .OrderBy(g => g.Min(x => x.r.ConfirmationDeadlineAt))
            .Select(g => g.Key)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDeductionTimeoutItemIdsAsync(
        DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.DeductionDeadline != null
                     && i.DeductionDeadline < utcNow
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal == null)
            .OrderBy(i => i.DeductionDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<Guid>> GetRenterResponseTimeoutItemIdsAsync(
    DateTimeOffset utcNow, int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal != null
                     && i.DeductionProposal.Status == DeductionProposalStatus.Submitted
                     && i.DeductionProposal.RenterResponseDeadline < utcNow)
            .OrderBy(i => i.DeductionProposal!.RenterResponseDeadline)
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDisputedItemIdsAsync(
        int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount == null
                     && i.DeductionProposal != null
                     && i.DeductionProposal.Status == DeductionProposalStatus.Disputed)
            .OrderBy(i => i.DeductionProposal!.RespondedAt)     // khiếu nại cũ nhất lên đầu
            .Select(i => i.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<Guid>> GetSettlementReadyItemIdsAsync(
    int batchSize, CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.PendingSettlement
                     && i.FinalDeductionAmount != null
                     && i.InventoryClosureConfirmedAt != null
                     && i.SettlementRef == null
                     && !i.RentalOrder!.OperationReceipts.Any(r =>
                            r.Action == RentalItem.SettleRentalAction && r.TargetItemId == i.Id))
            .OrderBy(i => i.FinalDeductionAmount)   // thứ tự ổn định; thay bằng mốc chốt nếu có
            .Select(i => i.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }
}
