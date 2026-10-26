namespace Dprental.AnalyticsService.Application.Dashboard.Revenue;

public enum RevenueGranularity
{
    Day,
    Week,
    Month
}

public static class RevenueGranularityParser
{
    public static bool TryParse(string? value, out RevenueGranularity granularity)
    {
        granularity = value?.Trim().ToLowerInvariant() switch
        {
            "day" => RevenueGranularity.Day,
            "week" => RevenueGranularity.Week,
            "month" => RevenueGranularity.Month,
            _ => (RevenueGranularity)(-1)
        };

        return Enum.IsDefined(granularity);
    }
}
