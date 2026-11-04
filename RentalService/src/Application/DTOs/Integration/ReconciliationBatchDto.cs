namespace Dprental.RentalService.Application.DTOs.Integration;

/// <summary>
/// DTO phân trang đối soát mục thuê hoàn tất từ RentalService phục vụ Full-Window Sweep Reconciliation.
/// </summary>
public sealed record ReconciliationBatchDto(
    IReadOnlyList<RentalItemReviewSnapshotDto> Items,
    string? NextCursor,
    bool HasMore);
