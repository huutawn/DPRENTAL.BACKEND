using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Assets;

/// <summary>
/// Thông tin phản hồi chi tiết của đơn vị thiết bị vật lý trong kho.
/// </summary>
public sealed record InventoryAssetResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("assetCode")] string AssetCode,
    [property: JsonPropertyName("serialNumber")] string? SerialNumber,
    [property: JsonPropertyName("manufactureYear")] int? ManufactureYear,
    [property: JsonPropertyName("acceptingBookings")] bool AcceptingBookings,
    [property: JsonPropertyName("suspensionReason")] string? SuspensionReason,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);
