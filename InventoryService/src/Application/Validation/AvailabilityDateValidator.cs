using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Validation;

/// <summary>
/// Bộ kiểm tra tính hợp lệ của cửa sổ ngày dùng chung cho các API Availability theo DPR-150.
/// </summary>
public static class AvailabilityDateValidator
{
    private static readonly Regex MonthRegex = new(@"^([0-9]{4})-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    /// <summary>
    /// Xác thực cửa sổ xem lịch cho Listing hoặc Asset:
    /// - Không cho phép gửi đồng thời cả month và from/to.
    /// - Nếu gửi month: bắt buộc định dạng YYYY-MM, trả đủ các ngày từ ngày 1 đến ngày cuối của tháng.
    /// - Nếu gửi from/to: bắt buộc cả hai, đúng thứ tự, và tối đa 6 tháng.
    /// - Nếu không gửi cả hai: mặc định cửa sổ 6 tháng tính từ today (an toàn tại năm 9999).
    /// </summary>
    public static Result ValidateCalendarWindow(
        string? month,
        DateOnly? fromDate,
        DateOnly? toDate,
        DateOnly today,
        out DateOnly validStart,
        out DateOnly validEnd)
    {
        validStart = default;
        validEnd = default;

        bool hasMonth = !string.IsNullOrWhiteSpace(month);
        bool hasFromOrTo = fromDate.HasValue || toDate.HasValue;

        if (hasMonth && hasFromOrTo)
        {
            return Error.Validation(
                "CONFLICTING_DATE_PARAMETERS",
                "Không được gửi đồng thời tham số 'month' và 'from'/'to'.");
        }

        if (hasMonth)
        {
            var trimmedMonth = month!.Trim();
            var match = MonthRegex.Match(trimmedMonth);
            if (!match.Success)
            {
                return Error.Validation(
                    "INVALID_MONTH_FORMAT",
                    "Định dạng tháng không hợp lệ. Vui lòng sử dụng định dạng YYYY-MM.");
            }

            if (!int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out int year) ||
                !int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out int monthNum) ||
                year < 1 || year > 9999 || monthNum < 1 || monthNum > 12)
            {
                return Error.Validation(
                    "INVALID_MONTH_FORMAT",
                    "Giá trị tháng hoặc năm không hợp lệ.");
            }

            int daysInMonth = DateTime.DaysInMonth(year, monthNum);
            validStart = new DateOnly(year, monthNum, 1);
            validEnd = new DateOnly(year, monthNum, daysInMonth);
            return Result.Success();
        }

        if (hasFromOrTo)
        {
            return ValidateDateWindow(fromDate, toDate, out validStart, out validEnd);
        }

        validStart = today;
        validEnd = today.Year == 9999 && today.Month > 6 ? DateOnly.MaxValue : today.AddMonths(6);
        return Result.Success();
    }

    /// <summary>
    /// Xác thực cửa sổ ngày: bắt buộc, không null, đúng thứ tự, và chân trời tối đa 6 tháng.
    /// An toàn trước tràn số tại DateOnly.MaxValue (năm 9999).
    /// </summary>
    public static Result ValidateDateWindow(
        DateOnly? startDate,
        DateOnly? endDate,
        out DateOnly validStart,
        out DateOnly validEnd)
    {
        validStart = default;
        validEnd = default;

        if (!startDate.HasValue || !endDate.HasValue)
        {
            return Error.Validation(
                "MISSING_DATE_PARAMETERS",
                "Ngày bắt đầu và ngày kết thúc là bắt buộc.");
        }

        if (startDate.Value > endDate.Value)
        {
            return Error.Validation(
                "INVALID_DATE_ORDER",
                "Ngày bắt đầu không thể sau ngày kết thúc.");
        }

        // Kiểm tra chân trời 6 tháng an toàn trước ArgumentOutOfRangeException tại năm 9999
        if (startDate.Value.Year == 9999 && startDate.Value.Month > 6)
        {
            // Mọi ngày hợp lệ của DateOnly đều <= 9999-12-31 (nằm trong 6 tháng từ startDate)
            validStart = startDate.Value;
            validEnd = endDate.Value;
            return Result.Success();
        }

        var maxEnd = startDate.Value.AddMonths(6);
        if (endDate.Value > maxEnd)
        {
            return Error.Validation(
                "EXCEEDED_HORIZON_LIMIT",
                "Khoảng thời gian yêu cầu không được vượt quá 6 tháng.");
        }

        validStart = startDate.Value;
        validEnd = endDate.Value;
        return Result.Success();
    }
}
