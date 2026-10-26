namespace Dprental.AnalyticsService.Common.Utils;

/// <summary>
/// Chuyển số tiền VND nhận từ event (kiểu decimal) sang số nguyên để lưu DB.
/// VND không có đơn vị nhỏ hơn đồng, nên số có phần lẻ là dữ liệu lỗi: báo lỗi, không làm tròn.
/// </summary>
public static class VndAmount
{
    public const string InvalidErrorCode = "ANALYTICS_AMOUNT_INVALID";

    public static Result<long> ToLong(decimal amount)
    {
        if (amount < 0)
        {
            return Error.Validation(InvalidErrorCode, "Số tiền VND không được âm.");
        }

        if (amount != decimal.Truncate(amount))
        {
            return Error.Validation(InvalidErrorCode, "Số tiền VND phải là số nguyên.");
        }

        if (amount > long.MaxValue)
        {
            return Error.Validation(InvalidErrorCode, "Số tiền VND vượt giới hạn lưu trữ.");
        }

        return (long)amount;
    }
}
