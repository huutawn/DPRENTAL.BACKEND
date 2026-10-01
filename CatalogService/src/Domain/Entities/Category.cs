namespace Dprental.CatalogService.Domain.Entities;

/// <summary>
/// Nhóm phân loại equipment. MVP dùng danh sách phẳng, chưa có phân cấp.
/// </summary>
public class Category
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Định danh dạng URL-friendly, duy nhất trong toàn bộ catalog.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Category ngừng hoạt động không nhận equipment mới và không cho publish.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public ICollection<Equipment> Equipments { get; } = new List<Equipment>();
}
