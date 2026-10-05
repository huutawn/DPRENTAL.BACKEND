using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Clients;

/// <summary>
/// Client tích hợp với CatalogService.
/// </summary>
public interface ICatalogServiceClient
{
    /// <summary>
    /// Tra cứu loại thiết bị theo ID và xác nhận đã ở trạng thái Published.
    /// Trả về validation error nếu không tồn tại hoặc chưa công khai.
    /// </summary>
    Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thẩm tra specification động theo schema version từ CatalogService.
    /// Hiện tại CatalogService chưa hỗ trợ và trả về kết quả NotSupported.
    /// </summary>
    Task<Result<CatalogSpecValidationResult>> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default);
}
