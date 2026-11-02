using System.Text.Json;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin chi tiết một EquipmentListing trong kết quả batch tối giản phục vụ Rental Service.
/// Tuyệt đối không chứa serial number, asset code, nội bộ calendar hay concurrency token.
/// </summary>
public sealed record EquipmentListingBatchItemResponse(
    Guid Id,
    Guid OwnerUserId,
    Guid EquipmentTypeId,
    string Name,
    Guid? ThumbnailMediaId,
    decimal DailyRate,
    string Currency,
    string Status,
    decimal DepositAmount,
    Guid BrandId,
    string Model,
    int SpecSchemaVersion,
    JsonElement SpecValues,
    EquipmentListingBatchWarehouseDto? Warehouse = null
);
