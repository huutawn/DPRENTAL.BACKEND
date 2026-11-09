using System.Collections.Generic;

namespace Dprental.RentalService.Application.DTOs.ReviewIntegration;

/// <summary>
/// DTO chứa danh sách đối soát mục thuê hoàn tất theo lô dựa trên keyset pagination.
/// </summary>
public sealed record RentalReviewReconciliationBatchDto(
    IReadOnlyList<RentalItemReviewSnapshotDto> Items,
    string? NextCursor,
    bool HasMore);
