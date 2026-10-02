using System.ComponentModel.DataAnnotations;

namespace Dprental.CatalogService.Application.DTOs.EquipmentType;

/// <summary>
/// Tạo equipment type mới ở trạng thái Draft.
/// Không có organizationId, dailyPrice, regionId, availability hay thông số của Supplier:
/// các dữ liệu đó thuộc Supplier Asset, không thuộc Canonical Catalog.
/// </summary>
public sealed record CreateEquipmentTypeRequest
{
    [Required]
    public Guid? CategoryId { get; init; }

    /// <summary>Mã canonical, duy nhất toàn hệ thống. Được chuẩn hóa: bỏ khoảng trắng hai đầu và viết hoa.</summary>
    [Required]
    [MaxLength(50)]
    public string? Code { get; init; }

    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    [MaxLength(2000)]
    public string? Description { get; init; }

    /// <summary>Type có yêu cầu SpecSchema hay không. Mặc định là không.</summary>
    public bool? RequiresSpecSchema { get; init; }
}

/// <summary>
/// Cập nhật một phần equipment type: field bỏ trống (null) được giữ nguyên.
/// Không có Status và các mốc thời gian vì client không được đổi trực tiếp.
/// </summary>
public sealed record UpdateEquipmentTypeRequest
{
    public Guid? CategoryId { get; init; }

    /// <summary>Chỉ đổi được khi type chưa từng publish.</summary>
    [MaxLength(50)]
    public string? Code { get; init; }

    [MaxLength(200)]
    public string? Name { get; init; }

    /// <summary>Gửi chuỗi rỗng để xóa giá trị hiện tại.</summary>
    [MaxLength(2000)]
    public string? Description { get; init; }

    public bool? RequiresSpecSchema { get; init; }
}
