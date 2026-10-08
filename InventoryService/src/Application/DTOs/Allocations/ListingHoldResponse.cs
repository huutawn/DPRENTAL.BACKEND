using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Phản hồi kết quả giữ chỗ tạm thời theo nhóm (Group Hold).
/// </summary>
public sealed record ListingHoldResponse(
    [property: JsonPropertyName("listingId")] Guid ListingId,
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("groupReference")] string GroupReference,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("allocations")] IReadOnlyList<AllocationResponse> Allocations,
    [property: JsonPropertyName("isReplay")] bool IsReplay
);
