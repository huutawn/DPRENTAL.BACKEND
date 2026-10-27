using Dprental.AnalyticsService.Application.Dashboard;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardDateRangeTests
{
    [Fact]
    public void VietnamDateRange_IncludesLateUtcTimeFromSameVietnamDay()
    {
        var range = DashboardDateRange.Create(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 11));
        var payment = new DateTimeOffset(2026, 11, 10, 17, 30, 0, TimeSpan.Zero);
        Assert.True(payment >= range.StartUtc && payment < range.EndUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 17, 0, 0, TimeSpan.Zero), range.StartUtc);
    }
}
