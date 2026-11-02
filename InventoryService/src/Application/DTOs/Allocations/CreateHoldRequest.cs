using System.Text.Json.Serialization;

namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Yêu cầu tạo mới một lượt giữ chỗ tạm thời (Hold) cho tài sản.
/// Từ chối các thuộc tính không xác định (Disallow unmapped members).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateHoldRequest(
    string SourceSystem,
    string ExternalReference,
    DateOnly StartDate,
    DateOnly EndDate,
    DateTimeOffset HoldExpiresAt,
    string? Notes = null
);
