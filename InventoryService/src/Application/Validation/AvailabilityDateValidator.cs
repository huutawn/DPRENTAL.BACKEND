using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Validation;

/// <summary>
/// Bộ kiểm tra tính hợp lệ của cửa sổ ngày dùng chung cho các API Availability theo DPR-150.
/// </summary>
public static class AvailabilityDateValidator
{
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
