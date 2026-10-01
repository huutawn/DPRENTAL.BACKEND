using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho đối tượng giá trị RentalPeriod theo đặc tả SRS v3.1.
/// Bao gồm kiểm tra thời lượng thuê (1, 30, 31 ngày), thứ tự ngày, giao nhau ngày biên và chân trời đặt trước.
/// </summary>
public sealed class RentalPeriodTests
{
    /// <summary>
    /// Kiểm thử thời lượng thuê 1 ngày (StartDate == EndDate) là hoàn toàn hợp lệ.
    /// </summary>
    [Fact]
    public void Create_OneDayDuration_Succeeds()
    {
        DateOnly start = new(2026, 10, 10);
        DateOnly end = new(2026, 10, 10);

        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.DurationDays);
    }

    /// <summary>
    /// Kiểm thử thời lượng thuê 30 ngày (tối đa cho phép) là hợp lệ.
    /// </summary>
    [Fact]
    public void Create_ThirtyDaysDuration_Succeeds()
    {
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 10, 30); // 30 ngày inclusive

        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsSuccess);
        Assert.Equal(30, result.Value.DurationDays);
    }

    /// <summary>
    /// Kiểm thử thời lượng thuê 31 ngày vượt quá giới hạn tối đa và bị từ chối.
    /// </summary>
    [Fact]
    public void Create_ThirtyOneDaysDuration_Fails()
    {
        DateOnly start = new(2026, 10, 1);
        DateOnly end = new(2026, 10, 31); // 31 ngày

        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_RENTAL_DURATION", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử ngày bắt đầu sau ngày kết thúc (sai thứ tự ngày) bị từ chối.
    /// </summary>
    [Fact]
    public void Create_StartDateAfterEndDate_Fails()
    {
        DateOnly start = new(2026, 10, 15);
        DateOnly end = new(2026, 10, 10);

        var result = RentalPeriod.Create(start, end);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", result.Error.Code);
    }

    /// <summary>
    /// Kiểm thử trường hợp hai khoảng thời gian chạm nhau đúng ngày biên (A.EndDate == B.StartDate) tính là trùng lặp (Overlap).
    /// </summary>
    [Fact]
    public void Overlaps_SameBoundaryDate_ReturnsTrue()
    {
        var periodA = new RentalPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
        var periodB = new RentalPeriod(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

        Assert.True(periodA.Overlaps(periodB));
        Assert.True(periodB.Overlaps(periodA));
    }

    /// <summary>
    /// Kiểm thử trường hợp hai khoảng thời gian liền kề không giao nhau (A.EndDate + 1 == B.StartDate) không bị trùng lặp.
    /// </summary>
    [Fact]
    public void Overlaps_AdjacentNonOverlapping_ReturnsFalse()
    {
        var periodA = new RentalPeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5));
        var periodB = new RentalPeriod(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 10));

        Assert.False(periodA.Overlaps(periodB));
        Assert.False(periodB.Overlaps(periodA));
    }

    /// <summary>
    /// Kiểm thử chân trời nghiệp vụ: ngày bắt đầu trước ngày hôm nay bị từ chối.
    /// </summary>
    [Fact]
    public void ValidateBusinessHorizon_StartDateInPast_Fails()
    {
        DateOnly today = new(2026, 10, 10);
        var period = new RentalPeriod(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12));

        var check = period.ValidateBusinessHorizon(today);

        Assert.True(check.IsFailure);
        Assert.Equal("START_DATE_IN_PAST", check.Error.Code);
    }

    /// <summary>
    /// Kiểm thử chân trời nghiệp vụ: ngày kết thúc vượt quá 6 tháng bị từ chối.
    /// </summary>
    [Fact]
    public void ValidateBusinessHorizon_ExceedsSixMonths_Fails()
    {
        DateOnly today = new(2026, 10, 1);
        DateOnly maxHorizon = today.AddMonths(6); // 2027-04-01
        var period = new RentalPeriod(maxHorizon, maxHorizon.AddDays(5)); // Vượt quá chân trời

        var check = period.ValidateBusinessHorizon(today);

        Assert.True(check.IsFailure);
        Assert.Equal("EXCEEDS_BOOKING_HORIZON", check.Error.Code);
    }

    /// <summary>
    /// Kiểm thử chân trời nghiệp vụ: khoảng thuê nằm hoàn toàn trong phạm vi 6 tháng từ hôm nay là hợp lệ.
    /// </summary>
    [Fact]
    public void ValidateBusinessHorizon_WithinSixMonths_Succeeds()
    {
        DateOnly today = new(2026, 10, 1);
        var period = new RentalPeriod(today, today.AddDays(10));

        var check = period.ValidateBusinessHorizon(today);

        Assert.True(check.IsSuccess);
    }
}
