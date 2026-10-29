using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Policies;

/// <remarks>
/// Tính mốc D+1 theo UTC+7 và cộng số giờ gia hạn do options cung cấp.
/// Policy trả mốc/điều kiện; service quyết định cảnh báo, đối soát hoặc hết hạn trong luồng có khóa và transaction.
/// </remarks>
public static class HandoverDeadlinePolicy
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static DateOnly VietnamDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(VietnamOffset).DateTime);

    public static DateTimeOffset NoticeAt(DateOnly expectedStartDate)
    {
        DateOnly nextDay = expectedStartDate.AddDays(1);
        return new DateTimeOffset(nextDay.Year, nextDay.Month, nextDay.Day, 0, 0, 0, VietnamOffset);
    }

    public static DateTimeOffset AutoExpireAt(DateOnly expectedStartDate, int graceHours)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(graceHours, 1);
        return NoticeAt(expectedStartDate).AddHours(graceHours);
    }

    public static bool IsNoticeDue(RentalItem item, DateTimeOffset now) =>
        item.Status == RentalItemStatus.AwaitingHandover && now >= NoticeAt(item.ExpectedStartDate);

    public static bool IsAutoExpireDue(RentalItem item, DateTimeOffset now, int graceHours) =>
        item.Status == RentalItemStatus.AwaitingHandover &&
        now >= AutoExpireAt(item.ExpectedStartDate, graceHours);

    public static DateOnly AutoExpireStartBefore(DateTimeOffset now, int graceHours) =>
        VietnamDate(now.AddHours(-graceHours));
}
