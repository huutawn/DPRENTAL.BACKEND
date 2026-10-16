using Dprental.InventoryService.Application.DTOs.Availability;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Dữ liệu cache của một tháng bao gồm danh sách các khoảng bận và mốc hết hạn nội bộ (UTC).
/// </summary>
public sealed record CachedMonthRanges(
    IReadOnlyList<BusyRangeDto> Ranges,
    DateTimeOffset ExpiresAtUtc
);
