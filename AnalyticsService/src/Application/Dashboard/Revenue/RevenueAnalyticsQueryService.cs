using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Dashboard.Revenue;

/// <summary>Reads period revenue and cash-flow facts without treating deposits as revenue.</summary>
public sealed class RevenueAnalyticsQueryService(IPaymentFinancialEventRepository paymentRepo)
{
    public async Task<RevenueAnalyticsResponse> GetAsync(
        DateOnly from,
        DateOnly to,
        RevenueGranularity granularity,
        Guid? supplierId = null,
        CancellationToken cancellationToken = default)
    {
        var range = DashboardDateRange.Create(from, to);

        if (!Enum.IsDefined(granularity))
        {
            throw new ArgumentOutOfRangeException(nameof(granularity));
        }

        var totals = await paymentRepo.GetRevenueBucketsAsync(
            range.StartUtc,
            range.EndUtc,
            granularity,
            supplierId,
            cancellationToken);

        List<RevenueAnalyticsBucket> buckets = CreateBuckets(from, to, granularity)
            .Select(period =>
            {
                totals.TryGetValue(period, out var values);
                values ??= new decimal[4];
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
