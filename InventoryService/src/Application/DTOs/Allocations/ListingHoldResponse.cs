
namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Phản hồi kết quả giữ chỗ tạm thời theo nhóm (Group Hold).
/// </summary>
public sealed record ListingHoldResponse(
    Guid ListingId,
    string SourceSystem,
    string GroupReference,
    int Quantity,
    IReadOnlyList<AllocationResponse> Allocations,
    bool IsReplay
);
