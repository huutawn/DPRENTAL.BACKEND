using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin chi tiết một EquipmentListing trong kết quả batch tối giản phục vụ Rental Service.
/// Tuyệt đối không chứa serial number, asset code, nội bộ calendar hay concurrency token.
/// </summary>
public sealed record EquipmentListingBatchItemResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ownerUserId")] Guid OwnerUserId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("thumbnailMediaId")] Guid? ThumbnailMediaId,
    [property: JsonPropertyName("dailyRate")] decimal DailyRate,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("depositAmount")] decimal DepositAmount,
    [property: JsonPropertyName("brandId")] Guid BrandId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("specSchemaVersion")] int SpecSchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("warehouse")] EquipmentListingBatchWarehouseDto? Warehouse = null
);
