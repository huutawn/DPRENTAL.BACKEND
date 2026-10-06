using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Domain.Caching;

/// <summary>
/// Khoảng ngày bận của tài sản (inclusive).
/// </summary>
public sealed record BusyRangeDto(
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate
);
