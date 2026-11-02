
namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Thông tin phản hồi chi tiết của lượt phân bổ tài sản.
/// Không để lộ navigation, owner, xmin hoặc lịch sử transitions.
/// </summary>
public sealed record AllocationResponse(
    Guid Id,
    Guid AssetId,
    string SourceSystem,
    string ExternalReference,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    DateTimeOffset? HoldExpiresAt,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
