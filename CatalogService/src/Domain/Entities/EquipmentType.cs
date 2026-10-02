using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>Định nghĩa loại thiết bị trong Catalog. Aggregate chính của Catalog.</summary>
public class EquipmentType
{
    /// <summary>Id do ứng dụng tự gán.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Organization sở hữu equipment type, lấy từ claim <c>o</c> của người tạo.
    /// Null với bản ghi dùng chung do Admin quản lý (gồm dữ liệu có trước khi thêm cột này).
    /// </summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Danh sách category mà equipment type này thuộc về.</summary>
    public ICollection<Category> Categories { get; set; } = new List<Category>();
    /// <summary>Mã thiết bị duy nhất trong toàn bộ Catalog.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Trạng thái vòng đời, lưu dạng chuỗi và chỉ thay đổi qua <see cref="Publish"/> và <see cref="Disable"/>.</summary>
    public EquipmentTypeStatus Status { get; private set; } = EquipmentTypeStatus.Draft;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Thời điểm publish gần nhất. Null khi chưa từng được publish.</summary>
    public DateTime? PublishedAt { get; private set; }

    public ICollection<EquipmentTypeMedia> Media { get; set; } = new List<EquipmentTypeMedia>();

    /// <summary>Đưa equipment type lên public catalog từ trạng thái Draft hoặc Disabled.</summary>
    public void Publish(DateTime utcNow)
    {
        if (Status == EquipmentTypeStatus.Published)
        {
            throw new InvalidOperationException("EquipmentType đang ở trạng thái Published.");
        }

        utcNow = AsUtc(utcNow);
        Status = EquipmentTypeStatus.Published;
        PublishedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>Vô hiệu hóa equipment type đang Published khỏi public catalog.</summary>
    public void Disable(DateTime utcNow)
    {
        if (Status != EquipmentTypeStatus.Published)
        {
            throw new InvalidOperationException("Chỉ có thể vô hiệu hóa equipment type đang ở trạng thái Published.");
        }

        Status = EquipmentTypeStatus.Disabled;
        UpdatedAt = AsUtc(utcNow);
    }

    /// <summary>PostgreSQL timestamptz chỉ nhận DateTime có Kind = Utc, nên chuẩn hóa trước khi lưu.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
