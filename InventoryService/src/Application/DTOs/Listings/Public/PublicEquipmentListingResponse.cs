using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Media;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Hợp đồng DTO công khai đại diện cho một EquipmentListing (Public Equipment Listing).
/// Đã được làm sạch các trường nội bộ, bảo mật và chỉ xuất hiện khi Status = Active.
/// </summary>
public sealed record PublicEquipmentListingResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid BrandId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("rentalRate")] RentalRateDto RentalRate,
    [property: JsonPropertyName("depositAmount")] decimal DepositAmount,
    [property: JsonPropertyName("media")] IReadOnlyList<MediaReferenceResponse> Media,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
    [property: JsonPropertyName("totalQuantity")] int TotalQuantity,
    [property: JsonPropertyName("availableQuantity")] int AvailableQuantity
);
