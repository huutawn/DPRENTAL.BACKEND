using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho đối tượng giá trị RentalPeriod trong target domain.
/// Kiểm tra thứ tự ngày (StartDate &lt;= EndDate), thời lượng inclusive, và overlap logic.
/// </summary>
public sealed class RentalPeriodTests
{
    [Fact]
    public void Create_OneDayDuration_Succeeds()
    {
        DateOnly date = new(2026, 10, 15);
        var result = RentalPeriod.Create(date, date);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.DurationDays);
    }

    [Fact]
    public void Create_ThirtyDaysDuration_Succeeds()
    {
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 10, 30);
        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsSuccess);
        Assert.Equal(30, result.Value.DurationDays);
    }

    [Fact]
    public void Create_MoreThanThirtyDaysDuration_SucceedsInInventoryDomain()
    {
        // Giới hạn 30 ngày được chuyển sang Rental Service; Inventory cho phép RentalPeriod bất kỳ miễn là StartDate <= EndDate.
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 11, 15);
        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsSuccess);
        Assert.Equal(46, result.Value.DurationDays);
    }

    [Fact]
    public void Create_StartDateAfterEndDate_Fails()
    {
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 5);
        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", result.Error.Code);
    }

    [Fact]
    public void Overlaps_SameBoundaryDate_ReturnsTrue()
    {
        RentalPeriod periodA = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
        RentalPeriod periodB = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

        Assert.True(periodA.Overlaps(periodB));
        Assert.True(periodB.Overlaps(periodA));
    }

    [Fact]
    public void Overlaps_AdjacentNonOverlapping_ReturnsFalse()
    {
        RentalPeriod periodA = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
        RentalPeriod periodB = new(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 10));

        Assert.False(periodA.Overlaps(periodB));
        Assert.False(periodB.Overlaps(periodA));
    }
}
