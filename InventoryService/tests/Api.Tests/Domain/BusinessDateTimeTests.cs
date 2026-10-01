using Dprental.InventoryService.Common.Utils;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho tiện ích BusinessDateTime theo múi giờ Việt Nam (UTC+7, Asia/Ho_Chi_Minh).
/// Kiểm tra chuyển đổi mốc thời gian quanh nửa đêm và tính toán thời điểm bắt đầu ngày UTC.
/// </summary>
public sealed class BusinessDateTimeTests
{
    /// <summary>
    /// Kiểm thử thời điểm bắt đầu ngày (00:00:00 giờ Việt Nam) được chuyển đổi chính xác sang UTC (17:00:00 ngày hôm trước).
    /// </summary>
    [Fact]
    public void StartOfDayUtc_ConvertsCorrectlyToUtc()
    {
        DateOnly date = new(2026, 10, 10);
        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(date);

        // 2026-10-10 00:00:00 +07:00 tương đương 2026-10-09 17:00:00 UTC
        Assert.Equal(TimeSpan.Zero, startOfDayUtc.Offset);
        Assert.Equal(2026, startOfDayUtc.Year);
        Assert.Equal(10, startOfDayUtc.Month);
        Assert.Equal(9, startOfDayUtc.Day);
        Assert.Equal(17, startOfDayUtc.Hour);
        Assert.Equal(0, startOfDayUtc.Minute);
    }

    /// <summary>
    /// Kiểm thử chuyển đổi mốc thời gian UTC quanh nửa đêm (trước và sau 17:00:00 UTC) sang ngày Việt Nam.
    /// </summary>
    [Fact]
    public void ToVietnamDate_AroundMidnight_ConvertsCorrectly()
    {
        // 16:59:59 UTC ngày 2026-10-09 -> tương ứng 23:59:59 ngày 2026-10-09 tại Việt Nam
        DateTimeOffset justBeforeMidnightVn = new(2026, 10, 9, 16, 59, 59, TimeSpan.Zero);
        DateOnly dateBefore = BusinessDateTime.ToVietnamDate(justBeforeMidnightVn);
        Assert.Equal(new DateOnly(2026, 10, 9), dateBefore);

        // 17:00:00 UTC ngày 2026-10-09 -> tương ứng 00:00:00 ngày 2026-10-10 tại Việt Nam
        DateTimeOffset exactlyMidnightVn = new(2026, 10, 9, 17, 0, 0, TimeSpan.Zero);
        DateOnly dateMidnight = BusinessDateTime.ToVietnamDate(exactlyMidnightVn);
        Assert.Equal(new DateOnly(2026, 10, 10), dateMidnight);

        // 17:00:01 UTC ngày 2026-10-09 -> tương ứng 00:00:01 ngày 2026-10-10 tại Việt Nam
        DateTimeOffset justAfterMidnightVn = new(2026, 10, 9, 17, 0, 1, TimeSpan.Zero);
        DateOnly dateAfter = BusinessDateTime.ToVietnamDate(justAfterMidnightVn);
        Assert.Equal(new DateOnly(2026, 10, 10), dateAfter);
    }
}
