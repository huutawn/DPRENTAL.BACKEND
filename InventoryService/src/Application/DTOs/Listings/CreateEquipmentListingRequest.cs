using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Yêu cầu tạo mới một EquipmentListing ở trạng thái Draft cùng một thiết bị vật lý ban đầu qua số serial.
/// Năm sản xuất (manufactureYear) là bắt buộc và được kế thừa cho thiết bị vật lý đầu tiên.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEquipmentListingRequest(
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid BrandId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("serialNumber")] string SerialNumber,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("rentalRate")] RentalRateDto RentalRate,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion = 1,
    [property: JsonPropertyName("mediaIds")] IReadOnlyList<Guid>? MediaIds = null,
    [property: JsonPropertyName("thumbnailMediaId")] Guid? ThumbnailMediaId = null
);
