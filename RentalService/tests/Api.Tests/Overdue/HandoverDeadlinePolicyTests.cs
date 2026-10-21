using Dprental.RentalService.Application.Policies;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Overdue;

public sealed class HandoverDeadlinePolicyTests
{
    [Fact]
    public void MultiDayRental_ExpiresExactly24HoursAfterNotice()
    {
        DateOnly start = new(2026, 10, 12);

        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(7)),
            HandoverDeadlinePolicy.NoticeAt(start));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.FromHours(7)),
            HandoverDeadlinePolicy.AutoExpireAt(start, 24));
    }

    [Fact]
    public void OneDayRental_AlsoGetsFull24HourGrace()
    {
        DateOnly day = new(2026, 10, 12);

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.FromHours(7)),
            HandoverDeadlinePolicy.AutoExpireAt(day, 24));
    }

    [Fact]
    public void ConfiguredGrace_UsesVietnamTimeAcrossUtcBoundary()
    {
        DateOnly start = new(2026, 10, 12);
        DateTimeOffset deadline = HandoverDeadlinePolicy.AutoExpireAt(start, 12);

        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.FromHours(7)), deadline);
        Assert.Equal(new DateOnly(2026, 10, 12), HandoverDeadlinePolicy.AutoExpireStartBefore(deadline.AddTicks(-1), 12));
        Assert.Equal(new DateOnly(2026, 10, 13), HandoverDeadlinePolicy.AutoExpireStartBefore(deadline, 12));
    }
}
