using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Contracts;

public sealed class RentalInventoryDateAdapterTests
{
    [Fact]
    public void ToInventoryDates_Rental10To11_MapsToInventory10To10()
    {
        DateOnly from = new(2026, 10, 10);
        DateOnly to = new(2026, 10, 11);

        Result<(DateOnly StartDate, DateOnly EndDate)> result =
            RentalInventoryDateAdapter.ToInventoryDates(from, to);

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2026, 10, 10), result.Value.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 10), result.Value.EndDate);
    }

    [Fact]
    public void ToRentalDates_Inventory10To10_MapsToRental10To11()
    {
        DateOnly startDate = new(2026, 10, 10);
        DateOnly endDate = new(2026, 10, 10);

        Result<(DateOnly From, DateOnly To)> result =
            RentalInventoryDateAdapter.ToRentalDates(startDate, endDate);

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2026, 10, 10), result.Value.From);
        Assert.Equal(new DateOnly(2026, 10, 11), result.Value.To);
    }

    [Fact]
    public void ConsecutiveIntervals_PreserveNoOverlapOnInventoryWire()
    {
        // Rental booking 1: Oct 10 -> Oct 15
        // Rental booking 2: Oct 15 -> Oct 20
        var b1 = RentalInventoryDateAdapter.ToInventoryDates(new(2026, 10, 10), new(2026, 10, 15)).Value;
        var b2 = RentalInventoryDateAdapter.ToInventoryDates(new(2026, 10, 15), new(2026, 10, 20)).Value;

        Assert.Equal(new DateOnly(2026, 10, 10), b1.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 14), b1.EndDate);

        Assert.Equal(new DateOnly(2026, 10, 15), b2.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 19), b2.EndDate);

        // Inventory inclusive end date of B1 (14th) is strictly before B2 start date (15th)
        Assert.True(b1.EndDate < b2.StartDate);
    }

    [Fact]
    public void ToInventoryDates_RejectsInvalidRange_WhenFromGreaterOrEqual()
    {
        Result<(DateOnly StartDate, DateOnly EndDate)> result =
            RentalInventoryDateAdapter.ToInventoryDates(new(2026, 10, 15), new(2026, 10, 15));

        Assert.True(result.IsFailure);
        Assert.Equal("Rental.InvalidDateRange", result.Error.Code);
    }

    [Fact]
    public void ValidateHoldDeadline_GuardsStartDateStartOfDayInAsiaHoChiMinh()
    {
        DateOnly startDate = new(2026, 10, 10);
        // StartDate 00:00 Asia/Ho_Chi_Minh is 2026-10-09 17:00 UTC (+07:00)
        DateTimeOffset startDateStartUtc = RentalInventoryDateAdapter.GetStartOfDayUtc(startDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 17, 0, 0, TimeSpan.Zero), startDateStartUtc);

        DateTimeOffset now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

        // Expiry at 16:30 UTC on Oct 9 is before 17:00 UTC -> Valid
        DateTimeOffset validExpiry = new(2026, 10, 9, 16, 30, 0, TimeSpan.Zero);
        Result validRes = RentalInventoryDateAdapter.ValidateHoldDeadline(now, validExpiry, startDate);
        Assert.True(validRes.IsSuccess);

        // Expiry at 17:00 UTC (exact start of day) or later -> Rejected
        DateTimeOffset lateExpiry = new(2026, 10, 9, 17, 0, 0, TimeSpan.Zero);
        Result lateRes = RentalInventoryDateAdapter.ValidateHoldDeadline(now, lateExpiry, startDate);
        Assert.True(lateRes.IsFailure);
        Assert.Equal("Inventory.HoldExpiresAtTooLate", lateRes.Error.Code);

        // Expiry in past -> Rejected
        Result pastRes = RentalInventoryDateAdapter.ValidateHoldDeadline(now, now.AddMinutes(-5), startDate);
        Assert.True(pastRes.IsFailure);
        Assert.Equal("Inventory.HoldExpired", pastRes.Error.Code);
    }
}
