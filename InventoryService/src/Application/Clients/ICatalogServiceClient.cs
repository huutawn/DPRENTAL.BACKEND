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
    /// Tra cứu thương hiệu theo ID từ CatalogService.
    /// </summary>
    Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thẩm tra specification động theo schema version từ CatalogService.
    /// </summary>
    Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy batch specification schemas của loại thiết bị từ CatalogService (gồm current schema và các version được yêu cầu).
    /// </summary>
    Task<Result<CatalogBatchSchemasResponse>> GetEquipmentTypeSchemasBatchAsync(Guid equipmentTypeId, IReadOnlyList<int> versions, CancellationToken cancellationToken = default);
}
