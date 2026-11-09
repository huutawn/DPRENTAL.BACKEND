using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed partial class RentalOrderRepository
{
    public async Task<RentalItemReviewSnapshotRecord?> GetReviewSnapshotRecordAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        return await _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new RentalItemReviewSnapshotRecord(
                i.RentalOrderId,
                i.Id,
                i.RentalOrder!.RenterId,
                i.SupplierId,
                i.EquipmentListingId,
                i.Status,
                i.CompletedAt,
                i.RentalOrder!.Version))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<RentalReviewReconciliationPageRecord> GetReviewReconciliationPageAsync(
        DateTimeOffset windowStart,
        DateTimeOffset? lastCompletedAt,
        Guid? lastItemId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.RentalItems
            .AsNoTracking()
            .Where(i => i.Status == RentalItemStatus.Completed && i.CompletedAt != null && i.CompletedAt >= windowStart);

        if (lastCompletedAt.HasValue && lastItemId.HasValue)
        {
            DateTimeOffset lTime = lastCompletedAt.Value;
            Guid lId = lastItemId.Value;
            query = query.Where(i => i.CompletedAt > lTime || (i.CompletedAt == lTime && i.Id > lId));
        }

        var candidateItems = await query
            .OrderBy(i => i.CompletedAt)
            .ThenBy(i => i.Id)
            .Take(limit + 1)
            .Select(i => new RentalItemReviewSnapshotRecord(
                i.RentalOrderId,
                i.Id,
                i.RentalOrder!.RenterId,
                i.SupplierId,
                i.EquipmentListingId,
                i.Status,
                i.CompletedAt,
                i.RentalOrder!.Version))
            .ToListAsync(cancellationToken);

        bool hasMore = candidateItems.Count > limit;
        IReadOnlyList<RentalItemReviewSnapshotRecord> resultItems = hasMore
            ? candidateItems.GetRange(0, limit)
            : candidateItems;

        return new RentalReviewReconciliationPageRecord(resultItems, hasMore);
    }
}
