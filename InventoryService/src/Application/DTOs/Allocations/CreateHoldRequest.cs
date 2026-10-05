using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu tạo mới một lượt giữ chỗ tạm thời (Hold) cho tài sản.
/// Từ chối các thuộc tính không xác định (Disallow unmapped members).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHoldRequest(
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("externalReference")] string ExternalReference,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("holdExpiresAt")] DateTimeOffset HoldExpiresAt,
    [property: JsonPropertyName("notes")] string? Notes = null
);
