using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Media;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Thông tin phản hồi chi tiết của một EquipmentListing cho chủ sở hữu hoặc quản trị viên.
/// Bao gồm năm sản xuất, danh sách media theo thứ tự, ảnh đại diện và derived totalQuantity.
/// </summary>
public sealed record EquipmentListingResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("ownerUserId")] Guid OwnerUserId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid BrandId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("media")] IReadOnlyList<MediaReferenceResponse> Media,
    [property: JsonPropertyName("thumbnailMediaId")] Guid? ThumbnailMediaId,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
    [property: JsonPropertyName("rentalRate")] RentalRateDto RentalRate,
    [property: JsonPropertyName("depositAmount")] decimal DepositAmount,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("totalQuantity")] int TotalQuantity,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);
