namespace Dprental.CatalogService.Domain.IRepositories;

/// <summary>
/// Đại diện Category đang hoạt động cùng danh sách ID các loại thiết bị đã Published liên kết.
/// </summary>
public sealed record CategoryWithPublishedTypes(
    Entities.Category Category,
    IReadOnlyList<Guid> PublishedTypeIds
);
