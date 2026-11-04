using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng use case duy nhất quản lý việc cập nhật điều kiện đánh giá (Review Eligibility Projection).
/// </summary>
public interface IReviewEligibilityService
{
    /// <summary>
    /// Cập nhật hoặc thêm mới projection điều kiện đánh giá từ authoritative snapshot.
    /// </summary>
    /// <param name="snapshot">Snapshot thông tin mục thuê authoritative từ RentalService.</param>
    /// <param name="ct">CancellationToken.</param>
    /// <returns>Result thành công hoặc lỗi tương ứng.</returns>
    Task<Result> UpsertEligibilityAsync(RentalItemReviewSnapshotDto snapshot, CancellationToken ct = default);
}
