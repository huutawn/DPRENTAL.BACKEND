using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.Serialization.Converters;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// DTO chứa thông tin giá thuê niêm yết của tài sản (dùng cho cả request và response).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssetRentalRateDto(
    [property: JsonPropertyName("dailyRate"), JsonConverter(typeof(StrictIntegerRateJsonConverter))] decimal? DailyRate,
    [property: JsonPropertyName("weeklyRate"), JsonConverter(typeof(StrictIntegerRateJsonConverter))] decimal? WeeklyRate = null,
    [property: JsonPropertyName("monthlyRate"), JsonConverter(typeof(StrictIntegerRateJsonConverter))] decimal? MonthlyRate = null,
    [property: JsonPropertyName("currency")] string? Currency = null
)
{
    /// <summary>
    /// Kiểm tra tính hợp lệ của DTO giá thuê: từ chối null/missing trường bắt buộc, giá trị âm, 0, phần lẻ, vượt giới hạn và sai tiền tệ.
    /// </summary>
    public Result Validate()
    {
        if (!DailyRate.HasValue)
        {
            return Error.Validation("DAILY_RATE_REQUIRED", "Giá thuê theo ngày (dailyRate) là bắt buộc.");
        }

        if (string.IsNullOrWhiteSpace(Currency))
        {
            return Error.Validation("CURRENCY_REQUIRED", "Đơn vị tiền tệ (currency) là bắt buộc.");
        }

        return AssetRentalRate.ValidateRateValues(DailyRate.Value, WeeklyRate, MonthlyRate, Currency);
    }
}
