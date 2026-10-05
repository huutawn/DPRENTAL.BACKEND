using Dprental.CatalogService.Common.Enums;

namespace Dprental.CatalogService.Domain.Entities;

/// <summary>Định nghĩa loại thiết bị trong Catalog. Aggregate chính của Catalog.</summary>
public class EquipmentType
{
    /// <summary>Id do ứng dụng tự gán.</summary>
    public Guid Id { get; set; }

    /// <summary>Phiên bản schema đặc tả kỹ thuật đang được áp dụng. Chỉ gán qua <see cref="ApplyPublishedSchema"/>.</summary>
    public int? CurrentSchemaVersion { get; private set; }
    /// <summary>Danh sách các phiên bản specification schema của equipment type.</summary>
    public ICollection<EquipmentTypeSpecSchema> SpecSchemas { get; set; } = new List<EquipmentTypeSpecSchema>();

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

        if (!CurrentSchemaVersion.HasValue)
        {
            throw new InvalidOperationException("EquipmentType cần có specification schema đã công khai trước khi publish.");
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
    /// <summary>Cập nhật thời điểm sửa đổi của equipment type.</summary>
    public void Touch(DateTime utcNow)
    {
        UpdatedAt = AsUtc(utcNow);
    }

    /// <summary>
    /// Áp dụng phiên bản schema đã công khai cho equipment type.
    /// Invariant: schema phải thuộc type này, đã Published và có version lớn hơn version hiện tại.
    /// </summary>
    public void ApplyPublishedSchema(EquipmentTypeSpecSchema schema, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (schema.EquipmentTypeId != Id)
        {
            throw new InvalidOperationException("Schema không thuộc loại thiết bị này.");
        }

        if (!schema.PublishedAt.HasValue)
        {
            throw new InvalidOperationException("Chỉ có thể áp dụng schema đã được công khai.");
        }

        if (CurrentSchemaVersion.HasValue && schema.Version <= CurrentSchemaVersion.Value)
        {
            throw new InvalidOperationException("Phiên bản schema mới phải lớn hơn phiên bản hiện tại.");
        }

        CurrentSchemaVersion = schema.Version;
        UpdatedAt = AsUtc(utcNow);
    }

    /// <summary>PostgreSQL timestamptz chỉ nhận DateTime có Kind = Utc, nên chuẩn hóa trước khi lưu.</summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
}
