using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Thông tin phản hồi chi tiết của tài sản trong kho.
/// </summary>
public sealed record InventoryAssetResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ownerUserId")] Guid OwnerUserId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid? BrandId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear,
    [property: JsonPropertyName("thumbnailMediaId")] Guid? ThumbnailMediaId,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("acceptingBookings")] bool AcceptingBookings,
    [property: JsonPropertyName("suspensionReason")] string? SuspensionReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);
