using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.ValueObjects;

/// <summary>
/// Đối tượng giá trị (Value Object) đại diện cho khoảng thời gian thuê thiết bị, gồm ngày bắt đầu và kết thúc (inclusive).
/// </summary>
[ComplexType]
public sealed record RentalPeriod
{
    /// <summary>
    /// Số ngày thuê tối thiểu cho phép trong một lượt thuê (bao gồm cả trường hợp thuê 1 ngày).
    /// </summary>
    public const int MinDurationDays = 1;

    /// <summary>
    /// Số ngày thuê tối đa cho phép trong một lượt thuê theo quy định SRS (30 ngày).
    /// </summary>
    public const int MaxDurationDays = 30;

    /// <summary>
    /// Ngày bắt đầu thời hạn thuê (tính theo ngày dương lịch tại Việt Nam).
    /// </summary>
    [Column("start_date")]
    public DateOnly StartDate { get; private set; }

    /// <summary>
    /// Ngày kết thúc thời hạn thuê (tính theo ngày dương lịch tại Việt Nam, bao gồm cả ngày này).
    /// </summary>
    [Column("end_date")]
    public DateOnly EndDate { get; private set; }

    /// <summary>
    /// Tổng số ngày thuê được tính theo hai đầu inclusive: (EndDate - StartDate + 1).
    /// </summary>
    [NotMapped]
    public int DurationDays => EndDate.DayNumber - StartDate.DayNumber + 1;

    /// <summary>
    /// Khởi tạo private phục vụ ORM EF Core.
    /// </summary>
    private RentalPeriod()
    {
    }

    /// <summary>
    /// Khởi tạo khoảng thời gian thuê với kiểm tra bất biến nghiệp vụ.
    /// </summary>
    /// <param name="startDate">Ngày bắt đầu thuê.</param>
    /// <param name="endDate">Ngày kết thúc thuê.</param>
    /// <exception cref="ArgumentException">Ném ra nếu ngày bắt đầu sau ngày kết thúc hoặc số ngày không nằm trong [1, 30].</exception>
    public RentalPeriod(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("Ngày bắt đầu thuê không thể sau ngày kết thúc.", nameof(startDate));
        }

        int duration = endDate.DayNumber - startDate.DayNumber + 1;
        if (duration < MinDurationDays || duration > MaxDurationDays)
        {
            throw new ArgumentException($"Thời lượng thuê phải từ {MinDurationDays} đến {MaxDurationDays} ngày. Thời lượng hiện tại: {duration} ngày.", nameof(endDate));
        }

        StartDate = startDate;
        EndDate = endDate;
    }

    /// <summary>
    /// Tạo khoảng thời gian thuê an toàn trả về đối tượng Result.
    /// </summary>
    /// <param name="startDate">Ngày bắt đầu thuê.</param>
    /// <param name="endDate">Ngày kết thúc thuê.</param>
    /// <returns>Result chứa RentalPeriod nếu hợp lệ, ngược lại trả về lỗi validation.</returns>
    public static Result<RentalPeriod> Create(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATE_ORDER", "Ngày bắt đầu thuê không thể sau ngày kết thúc.");
        }

        int duration = endDate.DayNumber - startDate.DayNumber + 1;
        if (duration < MinDurationDays || duration > MaxDurationDays)
        {
            return Error.Validation("INVALID_RENTAL_DURATION", $"Thời lượng thuê phải từ {MinDurationDays} đến {MaxDurationDays} ngày. Thời lượng thực tế: {duration} ngày.");
        }

        return new RentalPeriod(startDate, endDate);
    }

    /// <summary>
    /// Kiểm tra xem khoảng thời gian này có bị trùng lặp (overlap) với một khoảng thời gian thuê khác không.
    /// Hai khoảng trùng nhau khi: A.StartDate &lt;= B.EndDate &amp;&amp; A.EndDate &gt;= B.StartDate.
    /// </summary>
    /// <param name="other">Khoảng thời gian thuê cần so sánh.</param>
    /// <returns>True nếu có phần giao nhau, ngược lại False.</returns>
    public bool Overlaps(RentalPeriod other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StartDate <= other.EndDate && EndDate >= other.StartDate;
    }

    /// <summary>
    /// Kiểm tra xem khoảng thời gian này có bị trùng lặp với một khoảng ngày bất kỳ không.
    /// </summary>
    /// <param name="start">Ngày bắt đầu cần kiểm tra.</param>
    /// <param name="end">Ngày kết thúc cần kiểm tra.</param>
    /// <returns>True nếu có phần giao nhau, ngược lại False.</returns>
    public bool Overlaps(DateOnly start, DateOnly end)
    {
        return StartDate <= end && EndDate >= start;
    }

    /// <summary>
    /// Kiểm tra một ngày cụ thể có nằm trong khoảng thời gian thuê không (bao gồm cả hai ngày biên).
    /// </summary>
    /// <param name="date">Ngày cần kiểm tra.</param>
    /// <returns>True nếu ngày nằm trong khoảng, ngược lại False.</returns>
    public bool Contains(DateOnly date)
    {
        return date >= StartDate && date <= EndDate;
    }

    /// <summary>
    /// Kiểm tra khoảng thời gian thuê đối với ngày hiện tại và chân trời tối đa (Horizon).
    /// Phương thức này sử dụng ngày được truyền vào, không tự đọc clock toàn cục để đảm bảo tính thuần khiết domain.
    /// </summary>
    /// <param name="today">Ngày hiện tại tại Việt Nam.</param>
    /// <param name="maxMonthsAhead">Số tháng tối đa cho phép đặt trước (mặc định 6 tháng).</param>
    /// <returns>Result thành công nếu hợp lệ, ngược lại trả về lỗi validation.</returns>
    public Result ValidateBusinessHorizon(DateOnly today, int maxMonthsAhead = 6)
    {
        if (StartDate < today)
        {
            return Error.Validation("START_DATE_IN_PAST", "Ngày bắt đầu thuê không thể trước ngày hôm nay.");
        }

        DateOnly maxHorizon = today.AddMonths(maxMonthsAhead);
        if (EndDate > maxHorizon)
        {
            return Error.Validation("EXCEEDS_BOOKING_HORIZON", $"Ngày kết thúc thuê không thể vượt quá chân trời {maxMonthsAhead} tháng ({maxHorizon:yyyy-MM-dd}).");
        }

        return Result.Success();
    }
}
