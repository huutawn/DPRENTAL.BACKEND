namespace Dprental.InventoryService.Application.DTOs.Availability;

/// <summary>
/// DTO lưu trữ trạng thái khả dụng của một asset trong cửa sổ ngày xác định để cache theo từng asset.
/// </summary>
public sealed record AssetAvailabilityCheckCachedDto(
    bool IsAvailable,
    bool IsNotFound
);
