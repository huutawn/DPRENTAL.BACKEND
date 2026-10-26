using System.Data;
using Dprental.AnalyticsService.Application.Dashboard;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Dashboard.Revenue;

/// <summary>Reads settled platform-fee facts for the Admin revenue chart.</summary>
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
            RevenueGranularity.Day => "(\"CompletedAt\" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Week => "date_trunc('week', \"CompletedAt\" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            RevenueGranularity.Month => "date_trunc('month', \"CompletedAt\" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date",
            _ => throw new ArgumentOutOfRangeException(nameof(granularity))
        };

        var connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        var totals = new Dictionary<DateOnly, decimal>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {bucketExpression} AS period_start,
                       COALESCE(SUM("PlatformFeeAmount"), 0) AS platform_revenue
                FROM payment_financial_events
                WHERE "MessageType" = @message_type
                  AND "Currency" = 'VND'
                  AND "CompletedAt" >= @range_start
                  AND "CompletedAt" < @range_end
                GROUP BY period_start
                ORDER BY period_start;
                """;

            AddParameter(command, "message_type", PaymentAnalyticsEventTypes.RentalItemFinancialSettled);
            AddParameter(command, "range_start", range.StartUtc);
            AddParameter(command, "range_end", range.EndUtc);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                DateOnly periodStart = reader.GetFieldValue<DateOnly>(0);
                totals[periodStart] = reader.GetDecimal(1);
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
            .Select(period => new RevenueAnalyticsBucket(period, totals.GetValueOrDefault(period)))
            .ToList();

        return new RevenueAnalyticsResponse(
            from,
            to,
            granularity.ToString().ToLowerInvariant(),
            "VND",
            buckets.Sum(x => x.PlatformRevenue),
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
