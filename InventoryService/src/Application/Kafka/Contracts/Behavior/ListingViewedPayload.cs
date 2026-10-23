using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.Kafka.Contracts.Behavior;

/// <summary>
/// Payload sự kiện ListingViewed phát ra khi người dùng xem chi tiết listing Active công khai thành công.
/// Tuân thủ nghiêm ngặt schema v1: userId có thể null, equipmentListingId và equipmentTypeId bắt buộc.
/// </summary>
public sealed record ListingViewedPayload
{
    [JsonPropertyName("userId")]
    public Guid? UserId { get; init; }

    [JsonPropertyName("equipmentListingId")]
    public required Guid EquipmentListingId { get; init; }

    [JsonPropertyName("equipmentTypeId")]
    public required Guid EquipmentTypeId { get; init; }
}
