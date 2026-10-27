using Dprental.AnalyticsService.Application.Projections;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

public sealed class StatusGateTests
{
    [Theory]
    // lastVersion, currentRank, eventVersion, newRank, expected
    [InlineData(0L, 0, 1L, 1, true)]    // dòng mới
    [InlineData(2L, 2, 5L, 3, true)]    // event mới hơn
    [InlineData(5L, 3, 2L, 2, false)]   // event cũ đến muộn: không rollback
    [InlineData(3L, 3, 3L, 6, true)]    // cùng version, trạng thái xa hơn thắng
    [InlineData(3L, 6, 3L, 3, false)]   // cùng version, trạng thái gần hơn thua
    [InlineData(2L, 2, null, 3, false)] // không version: không ghi đè trạng thái có version
    [InlineData(0L, 1, null, 2, true)]  // không version, dòng chưa có version: được tiến lên
    [InlineData(0L, 2, null, 1, false)] // không version: không bao giờ lùi
    public void CanApply_FollowsVersionRules(long lastVersion, int currentRank, long? eventVersion, int newRank, bool expected)
    {
        Assert.Equal(expected, StatusGate.CanApply(lastVersion, currentRank, eventVersion, newRank));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UNKNOWN")]
    [InlineData("SOMETHING_LEGACY")]
    public void UnrecognisedStatus_RanksLowest_SoItCanStillBeUpdated(string legacy)
    {
        Assert.Equal(0, StatusGate.ItemRank(legacy));
        Assert.Equal(0, StatusGate.OrderRank(legacy));
    }
}
