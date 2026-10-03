namespace Dprental.InventoryService.Application.DTOs.Catalog;

/// <summary>
/// Trạng thái kết quả thẩm tra specification từ CatalogService.
/// </summary>
public enum CatalogSpecValidationStatus
{
    Valid = 0,
    Invalid = 1,
    NotSupported = 2
}
