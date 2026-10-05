using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Thông tin phản hồi chi tiết của lượt phân bổ tài sản.
/// Không để lộ navigation, owner, xmin hoặc lịch sử transitions.
/// </summary>
public sealed record AllocationResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("externalReference")] string ExternalReference,
    [property: JsonPropertyName("startDate")] DateOnly StartDate,
    [property: JsonPropertyName("endDate")] DateOnly EndDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("holdExpiresAt")] DateTimeOffset? HoldExpiresAt,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt
);
