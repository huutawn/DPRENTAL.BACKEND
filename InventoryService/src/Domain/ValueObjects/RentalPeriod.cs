using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.ValueObjects;

/// <summary>
/// Đối tượng giá trị (Value Object) đại diện cho khoảng thời gian phân bổ tài sản, gồm ngày bắt đầu và kết thúc (inclusive).
/// </summary>
[ComplexType]
public sealed record RentalPeriod
{
    /// <summary>
    /// Ngày bắt đầu thời hạn phân bổ (tính theo ngày dương lịch tại Việt Nam).
    /// </summary>
    [Column("start_date")]
    public DateOnly StartDate { get; private set; }

    /// <summary>
    /// Ngày kết thúc thời hạn phân bổ (tính theo ngày dương lịch tại Việt Nam, bao gồm cả ngày này).
    /// </summary>
    [Column("end_date")]
    public DateOnly EndDate { get; private set; }

    /// <summary>
    /// Tổng số ngày phân bổ được tính theo hai đầu inclusive: (EndDate - StartDate + 1).
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
    /// Khởi tạo khoảng thời gian phân bổ với kiểm tra bất biến StartDate &lt;= EndDate.
    /// </summary>
    /// <param name="startDate">Ngày bắt đầu.</param>
    /// <param name="endDate">Ngày kết thúc.</param>
    /// <exception cref="ArgumentException">Ném ra nếu ngày bắt đầu sau ngày kết thúc.</exception>
    public RentalPeriod(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
        {
            throw new ArgumentException("Ngày bắt đầu không thể sau ngày kết thúc.", nameof(startDate));
        }

        StartDate = startDate;
        EndDate = endDate;
    }

    /// <summary>
    /// Tạo khoảng thời gian phân bổ an toàn trả về đối tượng Result.
    /// </summary>
    /// <param name="startDate">Ngày bắt đầu.</param>
    /// <param name="endDate">Ngày kết thúc.</param>
    /// <returns>Result chứa RentalPeriod nếu hợp lệ, ngược lại trả về lỗi validation.</returns>
    public static Result<RentalPeriod> Create(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATE_ORDER", "Ngày bắt đầu không thể sau ngày kết thúc.");
        }

        return new RentalPeriod(startDate, endDate);
    }

    /// <summary>
    /// Kiểm tra xem khoảng thời gian này có bị trùng lặp (overlap) với một khoảng thời gian khác không.
    /// Hai khoảng trùng nhau khi: A.StartDate &lt;= B.EndDate &amp;&amp; A.EndDate &gt;= B.StartDate.
    /// </summary>
    public bool Overlaps(RentalPeriod other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return StartDate <= other.EndDate && EndDate >= other.StartDate;
    }

    /// <summary>
    /// Kiểm tra xem khoảng thời gian này có bị trùng lặp với một khoảng ngày bất kỳ không.
    /// </summary>
    public bool Overlaps(DateOnly start, DateOnly end)
    {
        return StartDate <= end && EndDate >= start;
    }

    /// <summary>
    /// Kiểm tra một ngày cụ thể có nằm trong khoảng thời gian không (bao gồm cả hai ngày biên).
    /// </summary>
    public bool Contains(DateOnly date)
    {
        return date >= StartDate && date <= EndDate;
    }
}
