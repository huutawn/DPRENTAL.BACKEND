using Dprental.AnalyticsService.Common;

namespace Dprental.AnalyticsService.Application.Errors;

/// <summary>
/// Lỗi dữ liệu khi chiếu event vào read model: message vào dead letter, không lưu gì.
/// Thông báo chỉ chứa mã/khóa, không chứa dữ liệu nhạy cảm.
/// </summary>
public static class ProjectionErrors
{
    public const string CurrencyInvalidCode = "ANALYTICS_CURRENCY_INVALID";
    public const string QuantityInvalidCode = "ANALYTICS_QUANTITY_INVALID";
    public const string FactConflictCode = "ANALYTICS_FACT_CONFLICT";
    public const string DuplicateItemCode = "ANALYTICS_DUPLICATE_ITEM";
    public const string FinancialFactsInvalidCode = "ANALYTICS_FINANCIAL_FACTS_INVALID";

    public const string SupportedCurrency = "VND";

    public static bool IsSupportedCurrency(string? currency) =>
        string.Equals(currency, SupportedCurrency, StringComparison.OrdinalIgnoreCase);

    public static Error CurrencyInvalid(string? currency) =>
        Error.Validation(CurrencyInvalidCode, $"Analytics chỉ nhận tiền {SupportedCurrency}, nhận được '{currency}'.");

    public static Error QuantityInvalid(Guid rentalItemId) =>
        Error.Validation(QuantityInvalidCode, $"Dòng thuê '{rentalItemId}' có số lượng không hợp lệ.");

    // Cùng khóa nghiệp vụ nhưng số liệu khác: lỗi phía producer, không ghi đè và không coi là trùng
    public static Error FactConflict(string logicalEventId) =>
        Error.Conflict(FactConflictCode, $"Sự kiện tài chính '{logicalEventId}' đã tồn tại với số liệu khác.");

    public static Error DuplicateItem(Guid rentalItemId) =>
        Error.Validation(DuplicateItemCode, $"Event chứa dòng thuê '{rentalItemId}' lặp lại.");

    public static Error FinancialFactsInvalid() =>
        Error.Validation(FinancialFactsInvalidCode, "Các số tiền trong event tài chính không nhất quán.");
}
