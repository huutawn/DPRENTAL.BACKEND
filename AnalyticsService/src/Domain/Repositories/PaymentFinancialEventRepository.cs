using System.Data;
using System.Data.Common;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

public sealed class PaymentFinancialEventRepository : IPaymentFinancialEventRepository
{
    private const string InvoicePaidType = "InvoicePaidFinancial";
    private const string SettledType = "RentalItemFinancialSettled";
    private const string RefundRequestedType = "RefundRequestedFinancial";
    private const string RefundCompletedType = "RefundCompletedFinancial";
    private const string PayoutType = "SupplierPayoutCompleted";

    private readonly AnalyticsServiceDbContext _context;

    public PaymentFinancialEventRepository(AnalyticsServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public Task<bool> ExistsMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _context.PaymentFinancialEvents.AnyAsync(x => x.MessageId == messageId, cancellationToken);

    public Task<PaymentFinancialEvent?> FindByLogicalEventIdAsync(string logicalEventId, CancellationToken cancellationToken = default) =>
        _context.PaymentFinancialEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LogicalEventId == logicalEventId, cancellationToken);

    public void Add(PaymentFinancialEvent financialEvent) => _context.PaymentFinancialEvents.Add(financialEvent);

    public async Task<FinancialFactMetrics> GetFinancialMetricsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        if (supplierId is null)
        {
            var invoices = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == InvoicePaidType &&
                x.PaidAt >= startUtc && x.PaidAt < endUtc);

            decimal grossRentalValue = await invoices.SumAsync(x => (decimal?)x.RentalAmount, cancellationToken) ?? 0m;
            decimal depositCollected = await invoices.SumAsync(x => (decimal?)x.DepositAmount, cancellationToken) ?? 0m;
            int paidOrders = await invoices.Select(x => x.RentalOrderId).Distinct().CountAsync(cancellationToken);

            var settled = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == SettledType &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc);

            decimal platformRevenue = await settled.SumAsync(x => (decimal?)x.PlatformFeeAmount, cancellationToken) ?? 0m;
            decimal finalDeductionAmount = await settled.SumAsync(x => (decimal?)x.FinalDeductionAmount, cancellationToken) ?? 0m;
            decimal supplierPayable = await settled.SumAsync(x => (decimal?)x.SupplierPayable, cancellationToken) ?? 0m;

            var refunds = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == RefundCompletedType &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            decimal refundAmount = await refunds.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

            var payouts = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == PayoutType &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            decimal payoutCompleted = await payouts.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

            decimal? pendingRefund = await GetPendingRefundInternalAsync(cancellationToken);

            return new FinancialFactMetrics(
                grossRentalValue,
                depositCollected,
                paidOrders,
                platformRevenue,
                finalDeductionAmount,
                supplierPayable,
                refundAmount,
                payoutCompleted,
                pendingRefund);
        }
        else
        {
            var supplierItems = _context.RentalItems.AsNoTracking().Where(x => x.SupplierId == supplierId.Value);
            var invoices = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == InvoicePaidType &&
                x.PaidAt >= startUtc && x.PaidAt < endUtc);

            var paidSupplierItems = from item in supplierItems
                                    join invoice in invoices on item.RentalOrderId equals invoice.RentalOrderId
                                    select item;

            decimal grossRentalValue = await paidSupplierItems.SumAsync(x => (decimal?)x.RentalAmount, cancellationToken) ?? 0m;
            decimal depositCollected = await paidSupplierItems.SumAsync(x => (decimal?)x.DepositAmount, cancellationToken) ?? 0m;
            int paidOrders = await paidSupplierItems.Select(x => x.RentalOrderId).Distinct().CountAsync(cancellationToken);

            var settled = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == SettledType &&
                x.SupplierId == supplierId.Value &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc);

            decimal platformRevenue = await settled.SumAsync(x => (decimal?)x.PlatformFeeAmount, cancellationToken) ?? 0m;
            decimal finalDeductionAmount = await settled.SumAsync(x => (decimal?)x.FinalDeductionAmount, cancellationToken) ?? 0m;
            decimal supplierPayable = await settled.SumAsync(x => (decimal?)x.SupplierPayable, cancellationToken) ?? 0m;

            var payouts = _context.PaymentFinancialEvents.AsNoTracking().Where(x =>
                x.Currency == "VND" &&
                x.MessageType == PayoutType &&
                x.SupplierId == supplierId.Value &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            decimal payoutCompleted = await payouts.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;

            return new FinancialFactMetrics(
                grossRentalValue,
                depositCollected,
                paidOrders,
                platformRevenue,
                finalDeductionAmount,
                supplierPayable,
                0m,
                payoutCompleted,
                null);
        }
    }

    private async Task<decimal?> GetPendingRefundInternalAsync(CancellationToken cancellationToken)
    {
        bool hasRequests = await _context.PaymentFinancialEvents.AsNoTracking().AnyAsync(x =>
            x.Currency == "VND" &&
            x.MessageType == RefundRequestedType &&
            x.RefundRef != null, cancellationToken);

        if (!hasRequests)
        {
            return null;
        }

        var requested = _context.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.Currency == "VND" && x.MessageType == RefundRequestedType && x.RefundRef != null)
            .GroupBy(x => x.RefundRef!)
            .Select(g => new { RefundRef = g.Key, Amount = g.Sum(x => x.Amount ?? 0m) });

        var completed = _context.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.Currency == "VND" && x.MessageType == RefundCompletedType && x.RefundRef != null)
            .GroupBy(x => x.RefundRef!)
            .Select(g => new { RefundRef = g.Key, Amount = g.Sum(x => x.Amount ?? 0m) });

        var balances = from request in requested
                       join comp in completed on request.RefundRef equals comp.RefundRef into compGroup
                       from comp in compGroup.DefaultIfEmpty()
                       select request.Amount - (comp == null ? 0m : comp.Amount);

        return await balances.SumAsync(balance => balance > 0m ? balance : 0m, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<DateOnly, decimal[]>> GetRevenueBucketsAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        RevenueGranularity granularity,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var totals = new Dictionary<DateOnly, decimal[]>();

        if (!_context.Database.IsRelational())
        {
            return CalculateRevenueBucketsInMemory(startUtc, endUtc, granularity, supplierId);
        }

        string bucketExpression = granularity switch
        {
            RevenueGranularity.Day => "(moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Week => "date_trunc('week', moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Month => "date_trunc('month', moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            _ => throw new ArgumentOutOfRangeException(nameof(granularity))
        };

        var connection = _context.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();

            if (supplierId is null)
            {
                command.CommandText = $"""
                    SELECT {bucketExpression} AS period_start, category, SUM(amount) AS amount
                    FROM (
                      SELECT 0 AS category, "PaidAt" AS moment, "RentalAmount" AS amount
                      FROM payment_financial_events
                      WHERE "MessageType" = @invoice_paid_type AND "Currency" = 'VND'
                        AND "PaidAt" >= @range_start AND "PaidAt" < @range_end
                      UNION ALL
                      SELECT 1, "CompletedAt", "PlatformFeeAmount"
                      FROM payment_financial_events
                      WHERE "MessageType" = @settled_type AND "Currency" = 'VND'
                        AND "CompletedAt" >= @range_start AND "CompletedAt" < @range_end
                      UNION ALL
                      SELECT 2, "CompletedAt", "Amount"
                      FROM payment_financial_events
                      WHERE "MessageType" = @refund_type AND "Currency" = 'VND'
                        AND "CompletedAt" >= @range_start AND "CompletedAt" < @range_end
                      UNION ALL
                      SELECT 3, "CompletedAt", "Amount"
                      FROM payment_financial_events
                      WHERE "MessageType" = @payout_type AND "Currency" = 'VND'
                        AND "CompletedAt" >= @range_start AND "CompletedAt" < @range_end
                    ) facts
                    GROUP BY period_start, category
                    ORDER BY period_start, category;
                    """;
            }
            else
            {
                command.CommandText = $"""
                    SELECT {bucketExpression} AS period_start, category, SUM(amount) AS amount
                    FROM (
                      SELECT 0 AS category, p."PaidAt" AS moment, i."RentalAmount" AS amount
                      FROM analytics_rental_items i
                      JOIN payment_financial_events p ON p."RentalOrderId" = i."RentalOrderId"
                      WHERE p."MessageType" = @invoice_paid_type AND p."Currency" = 'VND'
                        AND p."PaidAt" >= @range_start AND p."PaidAt" < @range_end
                        AND i."SupplierId" = @supplier_id
                      UNION ALL
                      SELECT 1, "CompletedAt", "PlatformFeeAmount"
                      FROM payment_financial_events
                      WHERE "MessageType" = @settled_type AND "Currency" = 'VND'
                        AND "SupplierId" = @supplier_id
                        AND "CompletedAt" >= @range_start AND "CompletedAt" < @range_end
                      UNION ALL
                      SELECT 3, "CompletedAt", "Amount"
                      FROM payment_financial_events
                      WHERE "MessageType" = @payout_type AND "Currency" = 'VND'
                        AND "SupplierId" = @supplier_id
                        AND "CompletedAt" >= @range_start AND "CompletedAt" < @range_end
                    ) facts
                    GROUP BY period_start, category
                    ORDER BY period_start, category;
                    """;
                AddParameter(command, "supplier_id", supplierId.Value);
            }

            AddParameter(command, "invoice_paid_type", InvoicePaidType);
            AddParameter(command, "settled_type", SettledType);
            AddParameter(command, "refund_type", RefundCompletedType);
            AddParameter(command, "payout_type", PayoutType);
            AddParameter(command, "range_start", startUtc);
            AddParameter(command, "range_end", endUtc);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                DateOnly periodStart = reader.GetFieldValue<DateOnly>(0);
                if (!totals.TryGetValue(periodStart, out var values))
                {
                    values = new decimal[4];
                    totals[periodStart] = values;
                }
                values[reader.GetInt32(1)] = reader.GetDecimal(2);
            }
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }

        return totals;
    }

    private Dictionary<DateOnly, decimal[]> CalculateRevenueBucketsInMemory(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        RevenueGranularity granularity,
        Guid? supplierId)
    {
        var totals = new Dictionary<DateOnly, decimal[]>();

        void Accumulate(DateOnly bucket, int category, decimal amount)
        {
            if (!totals.TryGetValue(bucket, out var values))
            {
                values = new decimal[4];
                totals[bucket] = values;
            }
            values[category] += amount;
        }

        if (supplierId is null)
        {
            var invoiceEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == InvoicePaidType && x.PaidAt >= startUtc && x.PaidAt < endUtc);
            foreach (var ev in invoiceEvents)
            {
                Accumulate(ToBucket(ev.PaidAt!.Value, granularity), 0, ev.RentalAmount ?? 0m);
            }

            var settledEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == SettledType && x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            foreach (var ev in settledEvents)
            {
                Accumulate(ToBucket(ev.CompletedAt!.Value, granularity), 1, ev.PlatformFeeAmount ?? 0m);
            }

            var refundEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == RefundCompletedType && x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            foreach (var ev in refundEvents)
            {
                Accumulate(ToBucket(ev.CompletedAt!.Value, granularity), 2, ev.Amount ?? 0m);
            }

            var payoutEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == PayoutType && x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            foreach (var ev in payoutEvents)
            {
                Accumulate(ToBucket(ev.CompletedAt!.Value, granularity), 3, ev.Amount ?? 0m);
            }
        }
        else
        {
            var items = _context.RentalItems.AsNoTracking()
                .Where(x => x.SupplierId == supplierId.Value);

            var invoices = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == InvoicePaidType && x.PaidAt >= startUtc && x.PaidAt < endUtc)
                .ToList()
                .GroupBy(x => x.RentalOrderId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            foreach (var item in items)
            {
                if (invoices.TryGetValue(item.RentalOrderId, out var inv))
                {
                    Accumulate(ToBucket(inv.PaidAt!.Value, granularity), 0, item.RentalAmount);
                }
            }

            var settledEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == SettledType && x.SupplierId == supplierId.Value && x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            foreach (var ev in settledEvents)
            {
                Accumulate(ToBucket(ev.CompletedAt!.Value, granularity), 1, ev.PlatformFeeAmount ?? 0m);
            }

            var payoutEvents = _context.PaymentFinancialEvents.AsNoTracking()
                .Where(x => x.Currency == "VND" && x.MessageType == PayoutType && x.SupplierId == supplierId.Value && x.CompletedAt >= startUtc && x.CompletedAt < endUtc);
            foreach (var ev in payoutEvents)
            {
                Accumulate(ToBucket(ev.CompletedAt!.Value, granularity), 3, ev.Amount ?? 0m);
            }
        }

        return totals;
    }

    private static DateOnly ToBucket(DateTimeOffset moment, RevenueGranularity granularity)
    {
        var vnTime = moment.ToOffset(TimeSpan.FromHours(7)).Date;
        var d = DateOnly.FromDateTime(vnTime);
        return granularity switch
        {
            RevenueGranularity.Day => d,
            RevenueGranularity.Week => d.AddDays(-((int)d.DayOfWeek == 0 ? 6 : (int)d.DayOfWeek - 1)),
            RevenueGranularity.Month => new DateOnly(d.Year, d.Month, 1),
            _ => d
        };
    }

    public async Task<IReadOnlyList<Guid>> GetSupplierCandidatesAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default) =>
        await _context.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.SupplierId != null && x.CompletedAt >= startUtc && x.CompletedAt < endUtc)
            .Select(x => x.SupplierId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, SupplierFinancialStatRow>> GetSupplierFinancialStatsAsync(
        IReadOnlyCollection<Guid> supplierIds,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.PaymentFinancialEvents.AsNoTracking()
            .Where(x => x.Currency == "VND" && x.SupplierId != null && supplierIds.Contains(x.SupplierId.Value) &&
                x.CompletedAt >= startUtc && x.CompletedAt < endUtc)
            .GroupBy(x => x.SupplierId!.Value)
            .Select(g => new
            {
                SupplierId = g.Key,
                Payable = g.Sum(x => x.MessageType == SettledType ? x.SupplierPayable : 0) ?? 0m,
                Paid = g.Sum(x => x.MessageType == PayoutType ? x.Amount : 0) ?? 0m
            }).ToListAsync(cancellationToken);

        return rows.ToDictionary(
            x => x.SupplierId,
            x => new SupplierFinancialStatRow(x.SupplierId, x.Payable, x.Paid));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
