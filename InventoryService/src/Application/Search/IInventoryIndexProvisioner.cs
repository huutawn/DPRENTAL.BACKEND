namespace Dprental.InventoryService.Application.Search;

/// <summary>
/// Giao diện khởi tạo và cấu hình chỉ mục tìm kiếm (settings, searchable/filterable/sortable attributes, typo tolerance).
/// </summary>
public interface IInventoryIndexProvisioner
{
    /// <summary>
    /// Đảm bảo tất cả các index (public listings, owner listings, owner assets) đã tồn tại và áp dụng cấu hình chuẩn.
    /// </summary>
    Task EnsureIndexesAndSettingsAsync(CancellationToken cancellationToken = default);
}
