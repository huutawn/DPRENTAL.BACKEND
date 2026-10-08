using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu giữ chỗ tạm thời theo nhóm (Group Hold) trên một EquipmentListing.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateListingHoldRequest(
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("holdExpiresAt")] DateTimeOffset HoldExpiresAt,
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("groupReference")] string GroupReference,
    [property: JsonPropertyName("notes")] string? Notes = null
);
