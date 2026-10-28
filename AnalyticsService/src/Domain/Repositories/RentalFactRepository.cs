using System.Data;
using System.Data.Common;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class RentalFactRepository : IRentalFactRepository
{
    private const string InvoicePaidType = "InvoicePaidFinancial";
    private readonly AnalyticsServiceDbContext _context;

    public RentalFactRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<RentalOrderFact?> FindOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
        _context.RentalOrders.FindAsync([rentalOrderId], cancellationToken).AsTask();

    public Task<RentalItemFact?> FindItemAsync(Guid rentalItemId, CancellationToken cancellationToken = default) =>
        _context.RentalItems.FindAsync([rentalItemId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<RentalItemFact>> FindItemsByOrderAsync(Guid rentalOrderId, CancellationToken cancellationToken = default) =>
        await _context.RentalItems.Where(item => item.RentalOrderId == rentalOrderId).ToListAsync(cancellationToken);

    public void AddOrder(RentalOrderFact order) => _context.RentalOrders.Add(order);

    public void AddItem(RentalItemFact item) => _context.RentalItems.Add(item);

    public async Task<OrderFactCounts> GetOrderMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        if (supplierId is null)
        {
            var orders = _context.RentalOrders.AsNoTracking();
            var items = _context.RentalItems.AsNoTracking();

            int total = await orders.CountAsync(x => x.CreatedAt >= startUtc && x.CreatedAt < endUtc, cancellationToken);
            int active = await GetActiveOrdersCountAsync(endUtc, null, cancellationToken);
            int completed = await orders.CountAsync(x => x.CompletedAt >= startUtc && x.CompletedAt < endUtc, cancellationToken);
            int cancelled = await orders.CountAsync(x => x.CancelledAt >= startUtc && x.CancelledAt < endUtc, cancellationToken);
            int expired = await orders.CountAsync(x => x.ExpiredAt >= startUtc && x.ExpiredAt < endUtc, cancellationToken);
            int rejected = await items.CountAsync(x => x.RejectedAt >= startUtc && x.RejectedAt < endUtc, cancellationToken);

            return new OrderFactCounts(total, active, completed, cancelled, expired, rejected);
        }
        else
        {
            var supplierItems = _context.RentalItems.AsNoTracking().Where(x => x.SupplierId == supplierId.Value);
            var orders = _context.RentalOrders.AsNoTracking();

            var supplierOrders = from item in supplierItems
                                 join order in orders on item.RentalOrderId equals order.RentalOrderId
                                 select order;

            int total = await supplierOrders
                .Where(x => x.CreatedAt >= startUtc && x.CreatedAt < endUtc)
                .Select(x => x.RentalOrderId)
                .Distinct()
                .CountAsync(cancellationToken);

            int active = await GetActiveOrdersCountAsync(endUtc, supplierId, cancellationToken);

            int completed = await supplierOrders
                .Where(x => x.CompletedAt >= startUtc && x.CompletedAt < endUtc)
                .Select(x => x.RentalOrderId)
                .Distinct()
                .CountAsync(cancellationToken);

            int cancelled = await supplierOrders
                .Where(x => x.CancelledAt >= startUtc && x.CancelledAt < endUtc)
                .Select(x => x.RentalOrderId)
                .Distinct()
                .CountAsync(cancellationToken);

            int expired = await supplierOrders
                .Where(x => x.ExpiredAt >= startUtc && x.ExpiredAt < endUtc)
                .Select(x => x.RentalOrderId)
                .Distinct()
                .CountAsync(cancellationToken);

            int rejected = await supplierItems
                .CountAsync(x => x.RejectedAt >= startUtc && x.RejectedAt < endUtc, cancellationToken);

            return new OrderFactCounts(total, active, completed, cancelled, expired, rejected);
        }
    }

    public async Task<int> GetActiveOrdersCountAsync(
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var orders = _context.RentalOrders.AsNoTracking();

        if (supplierId is null)
        {
            return await orders.CountAsync(x =>
                x.PaidAt != null && x.PaidAt < endUtc &&
                (x.CompletedAt == null || x.CompletedAt >= endUtc) &&
                (x.CancelledAt == null || x.CancelledAt >= endUtc) &&
                (x.ExpiredAt == null || x.ExpiredAt >= endUtc) &&
                ((x.RejectedAt == null && x.Status != "REJECTED") || x.RejectedAt >= endUtc), cancellationToken);
        }

        var items = _context.RentalItems.AsNoTracking().Where(x => x.SupplierId == supplierId.Value);
        return await (from item in items
                      join order in orders on item.RentalOrderId equals order.RentalOrderId
                      where order.PaidAt != null && order.PaidAt < endUtc &&
                            (order.CompletedAt == null || order.CompletedAt >= endUtc) &&
                            (order.CancelledAt == null || order.CancelledAt >= endUtc) &&
                            (order.ExpiredAt == null || order.ExpiredAt >= endUtc) &&
                            ((order.RejectedAt == null && order.Status != "REJECTED") || order.RejectedAt >= endUtc)
                      select order.RentalOrderId).Distinct().CountAsync(cancellationToken);
    }

    public async Task<RentalFactMetrics> GetRentalMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var items = _context.RentalItems.AsNoTracking();
        if (supplierId.HasValue)
        {
            items = items.Where(x => x.SupplierId == supplierId.Value);
        }

        var invoices = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
            x.Currency == "VND" &&
            x.MessageType == InvoicePaidType &&
            x.PaidAt >= startUtc && x.PaidAt < endUtc);

        var paidItems = from item in items
                        join invoice in invoices on item.RentalOrderId equals invoice.RentalOrderId
                        select item;

        int paidCount = await paidItems.CountAsync(cancellationToken);
        int rentedQuantity = await items
            .Where(x => x.HandoverAt >= startUtc && x.HandoverAt < endUtc)
            .SumAsync(x => (int?)x.Quantity, cancellationToken) ?? 0;
        int completedRentals = await items
            .CountAsync(x => x.CompletedAt >= startUtc && x.CompletedAt < endUtc, cancellationToken);
        decimal completedRentalValue = await items
            .Where(x => x.CompletedAt >= startUtc && x.CompletedAt < endUtc)
            .SumAsync(x => (decimal?)x.RentalAmount, cancellationToken) ?? 0m;

        return new RentalFactMetrics(paidCount, rentedQuantity, completedRentals, completedRentalValue);
    }

    public async Task<EquipmentRankingsResult> GetEquipmentRankingsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var invoices = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
            x.Currency == "VND" &&
            x.MessageType == InvoicePaidType &&
            x.PaidAt >= startUtc && x.PaidAt < endUtc);

        var paid = from item in _context.RentalItems.AsNoTracking()
                   join invoice in invoices on item.RentalOrderId equals invoice.RentalOrderId
                   select item;

        var typeRows = await paid.GroupBy(x => x.EquipmentTypeId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Max(x => x.EquipmentTypeName),
                Count = g.Count(),
                Quantity = g.Sum(x => x.HandoverAt != null && x.HandoverAt < endUtc ? x.Quantity : 0),
                Value = g.Sum(x => x.RentalAmount),
                Completed = g.Sum(x => x.CompletedAt != null && x.CompletedAt < endUtc ? 1 : 0)
            })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Quantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);

        var types = typeRows.Select(x => new EquipmentRankRow(
            x.Id, x.Name ?? "Không xác định", x.Count, x.Quantity, x.Value, x.Completed)).ToList();

        var listingRows = await paid.GroupBy(x => x.EquipmentListingId)
            .Select(g => new
            {
                Id = g.Key,
                Count = g.Count(),
                Quantity = g.Sum(x => x.HandoverAt != null && x.HandoverAt < endUtc ? x.Quantity : 0),
                Value = g.Sum(x => x.RentalAmount),
                Completed = g.Sum(x => x.CompletedAt != null && x.CompletedAt < endUtc ? 1 : 0)
            })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Quantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);

        var listings = listingRows.Select(x => new EquipmentRankRow(
            x.Id, x.Id.ToString("D"), x.Count, x.Quantity, x.Value, x.Completed)).ToList();

        var brandRows = await paid.GroupBy(x => x.BrandId)
            .Select(g => new
            {
                Id = g.Key,
                Name = g.Max(x => x.BrandName),
                Count = g.Count(),
                Quantity = g.Sum(x => x.HandoverAt != null && x.HandoverAt < endUtc ? x.Quantity : 0),
                Value = g.Sum(x => x.RentalAmount),
                Completed = g.Sum(x => x.CompletedAt != null && x.CompletedAt < endUtc ? 1 : 0)
            })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Quantity)
            .ThenBy(x => x.Id).Take(limit).ToListAsync(cancellationToken);

        var brands = brandRows.Select(x => new EquipmentRankRow(
            x.Id, x.Name ?? "Không xác định", x.Count, x.Quantity, x.Value, x.Completed)).ToList();

        return new EquipmentRankingsResult(types, listings, brands);
    }

    public async Task<IReadOnlyList<Guid>> GetSupplierCandidatesAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var rentalCandidates = from item in _context.RentalItems.AsNoTracking()
                               join order in _context.RentalOrders.AsNoTracking()
                                   on item.RentalOrderId equals order.RentalOrderId
                               where order.CreatedAt >= startUtc && order.CreatedAt < endUtc ||
                                   order.PaidAt >= startUtc && order.PaidAt < endUtc ||
                                   item.ConfirmedAt >= startUtc && item.ConfirmedAt < endUtc ||
                                   item.RejectedAt >= startUtc && item.RejectedAt < endUtc ||
                                   item.ExpiredAt >= startUtc && item.ExpiredAt < endUtc ||
                                   item.CompletedAt >= startUtc && item.CompletedAt < endUtc
                               select item.SupplierId;

        return await rentalCandidates.Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetSupplierRequestCountsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default) =>
        await (from item in _context.RentalItems.AsNoTracking()
               join order in _context.RentalOrders.AsNoTracking() on item.RentalOrderId equals order.RentalOrderId
               where supplierIds.Contains(item.SupplierId) && order.CreatedAt >= startUtc && order.CreatedAt < endUtc
               select new { item.SupplierId, item.RentalOrderId })
               .Distinct()
               .GroupBy(x => x.SupplierId)
               .Select(g => new { SupplierId = g.Key, Count = g.Count() })
               .ToDictionaryAsync(x => x.SupplierId, x => x.Count, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, SupplierRentalStatRow>> GetSupplierRentalStatsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var invoices = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
            x.Currency == "VND" &&
            x.MessageType == InvoicePaidType &&
            x.PaidAt >= startUtc && x.PaidAt < endUtc);

        var stats = await (from item in _context.RentalItems.AsNoTracking()
                           where supplierIds.Contains(item.SupplierId)
                           group item by item.SupplierId into g
                           select new
                           {
                               SupplierId = g.Key,
                               Accepted = g.Sum(x => x.ConfirmedAt >= startUtc && x.ConfirmedAt < endUtc ? 1 : 0),
                               Rejected = g.Sum(x => x.RejectedAt >= startUtc && x.RejectedAt < endUtc && x.RejectionReason == "SUPPLIER_REJECTED" ? 1 : 0),
                               Timeout = g.Sum(x => x.ExpiredAt >= startUtc && x.ExpiredAt < endUtc && x.ExpiryReason == "SUPPLIER_CONFIRMATION_EXPIRED" ? 1 : 0),
                               Completed = g.Sum(x => x.CompletedAt >= startUtc && x.CompletedAt < endUtc ? 1 : 0)
                           }).ToDictionaryAsync(x => x.SupplierId, cancellationToken);

        var paidAmounts = await (from item in _context.RentalItems.AsNoTracking()
                                 join invoice in invoices on item.RentalOrderId equals invoice.RentalOrderId
                                 where supplierIds.Contains(item.SupplierId)
                                 group item by item.SupplierId into g
                                 select new
                                 {
                                     SupplierId = g.Key,
                                     RentalValue = g.Sum(x => x.RentalAmount)
                                 }).ToDictionaryAsync(x => x.SupplierId, cancellationToken);

        var result = new Dictionary<Guid, SupplierRentalStatRow>();
        foreach (var id in supplierIds)
        {
            stats.TryGetValue(id, out var st);
            paidAmounts.TryGetValue(id, out var pa);
            result[id] = new SupplierRentalStatRow(
                id,
                st?.Accepted ?? 0,
                st?.Rejected ?? 0,
                st?.Timeout ?? 0,
                st?.Completed ?? 0,
                pa?.RentalValue ?? 0m);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<DateOnly, int[]>> GetOrderTrendPointsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var points = new Dictionary<DateOnly, int[]>();

        if (!_context.Database.IsRelational())
        {
            var orders = _context.RentalOrders.Local
                .Concat(_context.RentalOrders.AsNoTracking())
                .Distinct();

            void Accumulate(DateTimeOffset? moment, int category)
            {
                if (moment is null || moment < startUtc || moment >= endUtc) return;
                var vnDate = DateOnly.FromDateTime(moment.Value.ToOffset(TimeSpan.FromHours(7)).Date);
                if (!points.TryGetValue(vnDate, out var counts))
                {
                    counts = new int[5];
                    points[vnDate] = counts;
                }
                counts[category]++;
            }

            foreach (var o in orders)
            {
                Accumulate(o.CreatedAt, 0);
                Accumulate(o.PaidAt, 1);
                Accumulate(o.CompletedAt, 2);
                Accumulate(o.CancelledAt, 3);
                Accumulate(o.ExpiredAt, 4);
            }
            return points;
        }

        var connection = _context.Database.GetDbConnection();
        bool opened = connection.State != ConnectionState.Open;
        if (opened) await _context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT category, (moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day, COUNT(*)::integer
                FROM (
                  SELECT 0 AS category, "CreatedAt" AS moment FROM analytics_rental_orders
                  UNION ALL SELECT 1, "PaidAt" FROM analytics_rental_orders
                  UNION ALL SELECT 2, "CompletedAt" FROM analytics_rental_orders
                  UNION ALL SELECT 3, "CancelledAt" FROM analytics_rental_orders
                  UNION ALL SELECT 4, "ExpiredAt" FROM analytics_rental_orders
                ) t
                WHERE moment >= @start AND moment < @end
                GROUP BY category, day;
                """;
            AddParameter(command, "start", startUtc);
            AddParameter(command, "end", endUtc);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                DateOnly date = reader.GetFieldValue<DateOnly>(1);
                if (!points.TryGetValue(date, out var counts))
                {
                    counts = new int[5];
                    points[date] = counts;
                }
                counts[reader.GetInt32(0)] = reader.GetInt32(2);
            }
        }
        finally
        {
            if (opened) await _context.Database.CloseConnectionAsync();
        }

        return points;
    }

    public async Task<IReadOnlyList<Guid>> FindSupplierIdsByOrdersOrItemsAsync(
        IReadOnlyCollection<Guid> orderIds,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (orderIds.Count == 0 && itemIds.Count == 0) return [];

        if (!_context.Database.IsRelational())
        {
            var localItems = _context.RentalItems.Local.Concat(_context.RentalItems.AsNoTracking());
            return localItems
                .Where(x => (orderIds.Count > 0 && orderIds.Contains(x.RentalOrderId)) ||
                            (itemIds.Count > 0 && itemIds.Contains(x.RentalItemId)))
                .Select(x => x.SupplierId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }

        var query = _context.RentalItems.AsNoTracking();
        return await query
            .Where(x => (orderIds.Count > 0 && orderIds.Contains(x.RentalOrderId)) ||
                        (itemIds.Count > 0 && itemIds.Contains(x.RentalItemId)))
            .Select(x => x.SupplierId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
