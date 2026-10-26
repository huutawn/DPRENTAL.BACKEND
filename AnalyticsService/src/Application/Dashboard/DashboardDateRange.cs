namespace Dprental.AnalyticsService.Application.Dashboard;

/// <summary>Inclusive calendar dates in Vietnam; database predicates use a half-open UTC range.</summary>
public sealed record DashboardDateRange(DateOnly From, DateOnly To, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public static DashboardDateRange Create(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber >= 366 || to == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "Khoảng ngày phải hợp lệ và không quá 366 ngày.");
        }

        TimeSpan vietnamOffset = TimeSpan.FromHours(7);
        DateTimeOffset localStart = new(from.ToDateTime(TimeOnly.MinValue), vietnamOffset);
        DateTimeOffset localEnd = new(to.AddDays(1).ToDateTime(TimeOnly.MinValue), vietnamOffset);
        return new DashboardDateRange(from, to, localStart.ToUniversalTime(), localEnd.ToUniversalTime());
    }
}
