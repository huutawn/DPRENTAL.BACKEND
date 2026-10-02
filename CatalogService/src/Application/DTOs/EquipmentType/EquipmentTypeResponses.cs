namespace Dprental.CatalogService.Application.DTOs.EquipmentType;

/// <summary>Chi tiết equipment type cho màn hình quản lý của Admin, gồm cả type Draft và Disabled.</summary>
public sealed record EquipmentTypeManagementResponse(
    Guid Id,
    Guid CategoryId,
    string Code,
    string Name,
    string? Description,
    bool RequiresSpecSchema,
    string Status,
    bool IsCodeLocked,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc,
    DateTime? PublishedAtUtc,
    DateTime? DisabledAtUtc);

/// <summary>Equipment type đang công khai; đây là dữ liệu Supplier dùng để chọn type khi tạo asset.</summary>
public sealed record EquipmentTypeResponse(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string Code,
    string Name,
    string? Description,
    bool RequiresSpecSchema);
