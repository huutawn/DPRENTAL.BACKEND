namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Số đếm listing và máy của một chủ sở hữu, dùng để áp hạn mức bản nháp.
/// "Counted" không tính listing Inactive và máy ngừng nhận đặt; "All" tính tất cả.
/// </summary>
public sealed record OwnerInventoryCounts(int CountedListings, int AllListings, int CountedAssets, int AllAssets);
