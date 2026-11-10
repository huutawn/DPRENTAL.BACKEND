using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu giữ chỗ tạm thời theo nhóm (Group Hold) trên một EquipmentListing.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateListingHoldRequest(
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset HoldExpiresAt,
    string SourceSystem,
    string GroupReference,
    string? Notes = null
);
