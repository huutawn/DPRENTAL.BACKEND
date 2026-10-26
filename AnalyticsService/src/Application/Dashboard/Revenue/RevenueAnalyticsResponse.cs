namespace Dprental.AnalyticsService.Application.Dashboard.Revenue;

/// <summary>
/// Platform revenue is the platform fee recognized at settlement time. Rental and deposit
/// principal are reported elsewhere and are not included in this value.
/// </summary>
public sealed record RevenueAnalyticsResponse(
    DateOnly From,
    DateOnly To,
    string Granularity,
    string Currency,
    decimal TotalPlatformRevenue,
    IReadOnlyList<RevenueAnalyticsBucket> Buckets);

public sealed record RevenueAnalyticsBucket(DateOnly PeriodStart, decimal PlatformRevenue);
