using System.Text;
using System.Text.Json;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public partial class RentalOrderRepository
{
    public async Task<RentalItemReviewSnapshotRecord?> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _dbContext.RentalItems
            .AsNoTracking()
            .Include(i => i.RentalOrder)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.RentalOrderId == orderId, cancellationToken);

        if (item == null)
        {
            return null;
        }

        DateTimeOffset? completedAt = null;
        if (item.Status == RentalItemStatus.Completed)
        {
            completedAt = await _dbContext.RentalStateTransitions
                .AsNoTracking()
                .Where(t => t.RentalItemId == item.Id &&
                            t.ToState == "Completed" &&
                            t.FromState != "Completed")
                .OrderBy(t => t.OccurredAt)
                .Select(t => (DateTimeOffset?)t.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new RentalItemReviewSnapshotRecord(
            OrderId: item.RentalOrderId,
            ItemId: item.Id,
            RenterId: item.RentalOrder?.RenterId ?? Guid.Empty,
            SupplierId: item.SupplierId,
            EquipmentListingId: item.EquipmentListingId,
            Status: item.Status,
            CompletedAt: completedAt,
            OrderVersion: item.RentalOrder?.Version ?? 1);
    }

    public async Task<ReviewReconciliationBatchRecord> GetReviewReconciliationBatchAsync(
        string? cursor,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        int pageSize = limit <= 0 ? 100 : Math.Min(limit, 100);
        DateTimeOffset windowStart = now.AddDays(-30);
        DateTimeOffset? lastCompletedAt = null;
        Guid? lastItemId = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("WindowStart", out var wsProp) || root.TryGetProperty("windowStart", out wsProp))
                {
                    if (wsProp.TryGetDateTimeOffset(out var ws)) windowStart = ws;
                }
                if (root.TryGetProperty("LastCompletedAt", out var lcaProp) || root.TryGetProperty("lastCompletedAt", out lcaProp))
                {
                    if (lcaProp.TryGetDateTimeOffset(out var lca)) lastCompletedAt = lca;
                }
                if (root.TryGetProperty("LastItemId", out var liProp) || root.TryGetProperty("lastItemId", out liProp))
                {
                    if (liProp.TryGetGuid(out var li)) lastItemId = li;
                }
            }
            catch
            {
                // Malformed cursor handled gracefully
            }
        }

        var query = from item in _dbContext.RentalItems.AsNoTracking()
                    where item.Status == RentalItemStatus.Completed
                    let completedAt = _dbContext.RentalStateTransitions
                        .Where(t => t.RentalItemId == item.Id && t.ToState == "Completed" && t.FromState != "Completed")
                        .OrderBy(t => t.OccurredAt)
                        .Select(t => (DateTimeOffset?)t.OccurredAt)
                        .FirstOrDefault()
                    where completedAt != null && completedAt >= windowStart && completedAt <= now
                    select new
                    {
                        Item = item,
                        CompletedAt = completedAt.Value,
                        Order = item.RentalOrder
                    };

        if (lastCompletedAt.HasValue && lastItemId.HasValue)
        {
            query = query.Where(x =>
                x.CompletedAt > lastCompletedAt.Value ||
                (x.CompletedAt == lastCompletedAt.Value && x.Item.Id.CompareTo(lastItemId.Value) > 0));
        }

        var rawList = await query
            .OrderBy(x => x.CompletedAt)
            .ThenBy(x => x.Item.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        bool hasMore = rawList.Count > pageSize;
        var pageItems = rawList.Take(pageSize).ToList();

        string? nextCursor = null;
        if (hasMore && pageItems.Count > 0)
        {
            var last = pageItems[^1];
            var payload = new
            {
                WindowStart = windowStart,
                LastCompletedAt = last.CompletedAt,
                LastItemId = last.Item.Id
            };
            string nextCursorJson = JsonSerializer.Serialize(payload);
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(nextCursorJson));
        }

        var items = pageItems.Select(p => new RentalItemReviewSnapshotRecord(
            OrderId: p.Item.RentalOrderId,
            ItemId: p.Item.Id,
            RenterId: p.Order?.RenterId ?? Guid.Empty,
            SupplierId: p.Item.SupplierId,
            EquipmentListingId: p.Item.EquipmentListingId,
            Status: p.Item.Status,
            CompletedAt: p.CompletedAt,
            OrderVersion: p.Order?.Version ?? 1)).ToList();

        return new ReviewReconciliationBatchRecord(items, nextCursor, hasMore);
    }
}
