using Dprental.AnalyticsService.Common;

namespace Dprental.AnalyticsService.Application.Errors;

/// <summary>
/// Lỗi khi chiếu event vào read model. Mọi lỗi ở đây khiến message bị đưa vào dead letter, không lưu gì.
/// Thông báo lỗi chỉ chứa mã/khóa, không chứa dữ liệu nhạy cảm.
/// </summary>
public static class ProjectionErrors
{
    public const string CurrencyInvalidCode = "ANALYTICS_CURRENCY_INVALID";
    public const string FactConflictCode = "ANALYTICS_FACT_CONFLICT";
    public const string DuplicateItemCode = "ANALYTICS_DUPLICATE_ITEM";

    public const string SupportedCurrency = "VND";

    public static Error CurrencyInvalid(string currency) =>
        Error.Validation(CurrencyInvalidCode, $"Analytics chỉ nhận tiền {SupportedCurrency}, nhận được '{currency}'.");

    // Cùng khóa tự nhiên nhưng số liệu khác: lỗi phía producer, không ghi đè
    public static Error FactConflict(string fact, string key) =>
        Error.Conflict(FactConflictCode, $"{fact} '{key}' đã tồn tại với số liệu khác.");

    public static Error DuplicateItem(string eventName, Guid rentalItemId) =>
        Error.Validation(DuplicateItemCode, $"{eventName} chứa dòng thuê '{rentalItemId}' lặp lại.");
}
