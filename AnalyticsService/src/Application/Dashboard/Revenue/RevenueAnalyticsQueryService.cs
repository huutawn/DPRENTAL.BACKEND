using System.Data;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Dashboard.Revenue;

/// <summary>Reads period revenue and cash-flow facts without treating deposits as revenue.</summary>
public sealed class RevenueAnalyticsQueryService(AnalyticsServiceDbContext dbContext)
{
    public async Task<RevenueAnalyticsResponse> GetAsync(
        DateOnly from,
        DateOnly to,
        RevenueGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        var range = DashboardDateRange.Create(from, to);

        if (!Enum.IsDefined(granularity))
        {
            throw new ArgumentOutOfRangeException(nameof(granularity));
        }

        // Only enum-selected SQL fragments are interpolated. User input is always a DB parameter.
        string bucketExpression = granularity switch
        {
            RevenueGranularity.Day => "(moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Week => "date_trunc('week', moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Month => "date_trunc('month', moment AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            _ => throw new ArgumentOutOfRangeException(nameof(granularity))
        };

        var connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        var totals = new Dictionary<DateOnly, decimal[]>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {bucketExpression} AS period_start, category, SUM(amount) AS amount
                FROM (
                  SELECT 0 AS category, o."PaidAt" AS moment, i."RentalAmount" AS amount
                  FROM analytics_rental_orders o
                  JOIN analytics_rental_items i ON i."RentalOrderId" = o."RentalOrderId"
                  WHERE o."PaidAt" >= @range_start AND o."PaidAt" < @range_end
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

            AddParameter(command, "settled_type", PaymentAnalyticsEventTypes.RentalItemFinancialSettled);
            AddParameter(command, "refund_type", PaymentAnalyticsEventTypes.RefundCompletedFinancial);
            AddParameter(command, "payout_type", PaymentAnalyticsEventTypes.SupplierPayoutCompleted);
            AddParameter(command, "range_start", range.StartUtc);
            AddParameter(command, "range_end", range.EndUtc);

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
                await dbContext.Database.CloseConnectionAsync();
            }
        }

        List<RevenueAnalyticsBucket> buckets = CreateBuckets(from, to, granularity)
            .Select(period =>
            {
                decimal[] values = totals.GetValueOrDefault(period) ?? new decimal[4];
                return new RevenueAnalyticsBucket(period, values[0], values[1], values[2], values[3]);
            })
            .ToList();

        return new RevenueAnalyticsResponse(
            from,
            to,
            granularity.ToString().ToLowerInvariant(),
            "VND",
            buckets.Sum(x => x.PlatformRevenue),
            buckets.Sum(x => x.GrossRentalValue),
            buckets.Sum(x => x.RefundAmount),
            buckets.Sum(x => x.SupplierPayout),
            buckets);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static IEnumerable<DateOnly> CreateBuckets(
        DateOnly from,
        DateOnly to,
        RevenueGranularity granularity)
    {
        DateOnly current = granularity switch
        {
            RevenueGranularity.Day => from,
            RevenueGranularity.Week => StartOfWeek(from),
            RevenueGranularity.Month => new DateOnly(from.Year, from.Month, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(granularity))
        };

        DateOnly end = granularity switch
        {
            RevenueGranularity.Day => to,
            RevenueGranularity.Week => StartOfWeek(to),
            RevenueGranularity.Month => new DateOnly(to.Year, to.Month, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(granularity))
        };

        while (current <= end)
        {
            yield return current;
            current = granularity switch
            {
                RevenueGranularity.Day => current.AddDays(1),
                RevenueGranularity.Week => current.AddDays(7),
                RevenueGranularity.Month => current.AddMonths(1),
                _ => throw new ArgumentOutOfRangeException(nameof(granularity))
            };
        }
    }

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
