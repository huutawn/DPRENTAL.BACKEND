using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

public static class RentalInventoryDateAdapter
{
    public static readonly TimeZoneInfo BusinessTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    /// <summary>
    /// Converts Rental interval [from, to) (exclusive end)
    /// to Inventory interval [startDate, endDate] (inclusive end: startDate = from, endDate = to - 1 day).
    /// </summary>
    public static Result<(DateOnly StartDate, DateOnly EndDate)> ToInventoryDates(DateOnly from, DateOnly to)
    {
        if (from >= to)
        {
            return Result.Failure<(DateOnly, DateOnly)>(
                Error.Validation("Rental.InvalidDateRange", "Ngày bắt đầu phải trước ngày kết thúc thuê."));
        }

        DateOnly startDate = from;
        DateOnly endDate = to.AddDays(-1);
        return Result.Success((startDate, endDate));
    }

    /// <summary>
    /// Converts Inventory inclusive interval [startDate, endDate]
    /// back to Rental interval [from, to) (exclusive end: from = startDate, to = endDate + 1 day).
    /// </summary>
    public static Result<(DateOnly From, DateOnly To)> ToRentalDates(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
        {
            return Result.Failure<(DateOnly, DateOnly)>(
                Error.Validation("Inventory.InvalidDateRange", "StartDate không được sau EndDate."));
        }

        DateOnly from = startDate;
        DateOnly to = endDate.AddDays(1);
        return Result.Success((from, to));
    }

    /// <summary>
    /// Converts a UTC timestamp to business date in Asia/Ho_Chi_Minh.
    /// </summary>
    public static DateOnly ToBusinessDate(DateTimeOffset utcTimestamp)
    {
        DateTimeOffset inTz = TimeZoneInfo.ConvertTime(utcTimestamp, BusinessTimeZone);
        return DateOnly.FromDateTime(inTz.DateTime);
    }

    /// <summary>
    /// Computes the exact start of day (00:00:00) for a business date in Asia/Ho_Chi_Minh, represented as UTC DateTimeOffset.
    /// </summary>
    public static DateTimeOffset GetStartOfDayUtc(DateOnly businessDate)
    {
        DateTime localStart = businessDate.ToDateTime(TimeOnly.MinValue);
        TimeSpan offset = BusinessTimeZone.GetUtcOffset(localStart);
        return new DateTimeOffset(localStart, offset).ToUniversalTime();
    }

    /// <summary>
    /// Validates Inventory hold expiry rule: now &lt; HoldExpiresAt &lt; StartDate 00:00:00 Asia/Ho_Chi_Minh.
    /// </summary>
    public static Result ValidateHoldDeadline(DateTimeOffset utcNow, DateTimeOffset holdExpiresAt, DateOnly startDate)
    {
        if (holdExpiresAt <= utcNow)
        {
            return Result.Failure(
                Error.Validation("Inventory.HoldExpired", "HoldExpiresAt phải lớn hơn thời điểm hiện tại."));
        }

        DateTimeOffset startDateStartUtc = GetStartOfDayUtc(startDate);
        if (holdExpiresAt >= startDateStartUtc)
        {
            return Result.Failure(
                Error.Validation("Inventory.HoldExpiresAtTooLate", "HoldExpiresAt phải trước 00:00 ngày StartDate theo giờ Asia/Ho_Chi_Minh."));
        }

        return Result.Success();
    }
}
