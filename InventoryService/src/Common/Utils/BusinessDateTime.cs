namespace Dprental.InventoryService.Common.Utils;

/// <summary>
/// Tiện ích xử lý múi giờ và mốc thời gian nghiệp vụ theo múi giờ Việt Nam (Asia/Ho_Chi_Minh, UTC+7).
/// </summary>
public static class BusinessDateTime
{
    /// <summary>UTC instant at PostgreSQL microsecond precision, stable across persistence roundtrips.</summary>
    public static DateTimeOffset NormalizeUtc(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>
    /// Định danh múi giờ chuẩn IANA của Việt Nam.
    /// </summary>
    public const string VietnamTimeZoneId = "Asia/Ho_Chi_Minh";

    /// <summary>
    /// Đối tượng múi giờ Việt Nam (UTC+7, không có Daylight Saving Time).
    /// </summary>
    public static readonly TimeZoneInfo VietnamTimeZone = ResolveVietnamTimeZone();

    private static TimeZoneInfo ResolveVietnamTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(VietnamTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                // Hỗ trợ môi trường Windows cũ với id chuẩn Windows
                return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            }
            catch
            {
                // Fallback tùy chỉnh nếu hệ thống không hỗ trợ registry timezone
                return TimeZoneInfo.CreateCustomTimeZone(VietnamTimeZoneId, TimeSpan.FromHours(7), "ICT", "ICT");
            }
        }
    }

    /// <summary>
    /// Chuyển đổi một thời điểm UTC sang ngày nghiệp vụ (DateOnly) theo múi giờ Việt Nam.
    /// </summary>
    /// <param name="utcDateTime">Thời điểm UTC cần chuyển đổi.</param>
    /// <returns>Ngày tương ứng tại Việt Nam.</returns>
    public static DateOnly ToVietnamDate(DateTimeOffset utcDateTime)
    {
        var localDateTime = TimeZoneInfo.ConvertTime(utcDateTime, VietnamTimeZone);
        return DateOnly.FromDateTime(localDateTime.DateTime);
    }

    /// <summary>
    /// Lấy thời điểm bắt đầu ngày (00:00:00) theo múi giờ Việt Nam và chuyển đổi sang thời điểm UTC.
    /// </summary>
    /// <param name="date">Ngày cần lấy thời điểm bắt đầu.</param>
    /// <returns>Thời điểm UTC tương ứng với 00:00:00 giờ Việt Nam.</returns>
    public static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var localDateTime = date.ToDateTime(TimeOnly.MinValue);
        var offset = VietnamTimeZone.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset).ToUniversalTime();
    }
}
