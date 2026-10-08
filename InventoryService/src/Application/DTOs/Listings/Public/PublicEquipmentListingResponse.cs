using System.Text.Json;
using System.Text.Json.Serialization;
using Dprental.InventoryService.Application.DTOs.Assets;

namespace Dprental.InventoryService.Application.DTOs.Listings;

/// <summary>
/// Hợp đồng DTO công khai đại diện cho một EquipmentListing (Public Equipment Listing).
/// Đã được làm sạch các trường nội bộ, bảo mật và chỉ xuất hiện khi Status = Active.
/// </summary>
public sealed record PublicEquipmentListingResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("brandId")] Guid BrandId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("specValues")] JsonElement SpecValues,
    [property: JsonPropertyName("rentalRate")] RentalRateDto RentalRate,
    [property: JsonPropertyName("thumbnailUrl")] string? ThumbnailUrl,
    [property: JsonPropertyName("totalQuantity")] int TotalQuantity,
    [property: JsonPropertyName("availableQuantity")] int AvailableQuantity
);
