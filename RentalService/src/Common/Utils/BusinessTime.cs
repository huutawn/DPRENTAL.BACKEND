namespace Dprental.RentalService.Common.Utils;

/// <summary>
/// Quy đổi thời điểm UTC sang ngày nghiệp vụ theo giờ Việt Nam (UTC+7, không có DST).
/// </summary>
public static class BusinessTime
{
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(7);

    public static DateOnly Today(DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(utcNow.ToOffset(UtcOffset).DateTime);
}
