namespace Dprental.ReviewService.Application.DTOs.Rental;

/// <summary>
/// DTO phân trang đối soát mục thuê hoàn tất từ RentalService (/internal/orders/review-reconciliation).
/// </summary>
public sealed record ReconciliationBatchDto(
    IReadOnlyList<RentalItemReviewSnapshotDto> Items,
    string? NextCursor,
    bool HasMore);
