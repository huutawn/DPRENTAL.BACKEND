using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Yêu cầu tạo mới tài sản trong kho.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInventoryAssetRequest(
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid? BrandId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("rentalRate")] AssetRentalRateDto? RentalRate = null,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber = null,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear = null,
    [property: JsonPropertyName("thumbnailMediaId")] Guid? ThumbnailMediaId = null
);
