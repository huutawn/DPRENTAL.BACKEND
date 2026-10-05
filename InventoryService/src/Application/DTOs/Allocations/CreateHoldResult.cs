namespace Dprental.InventoryService.Application.DTOs.Allocations;

/// <summary>
/// Kết quả tạo lượt giữ chỗ (Hold), chứa DTO phản hồi và cờ phân biệt tạo mới (201) hay replay (200).
/// </summary>
public sealed record CreateHoldResult(
    AllocationResponse Allocation,
    bool IsReplay
);
