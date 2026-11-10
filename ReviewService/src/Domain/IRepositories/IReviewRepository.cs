using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy vấn thực thể Review.
/// </summary>
public interface IReviewRepository
{
    /// <summary>
    /// Tìm đánh giá theo khóa chính (UUID).
    /// </summary>
    Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thêm mới một bản ghi đánh giá vào cơ sở dữ liệu.
    /// </summary>
    Task AddAsync(Review review, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cập nhật thay đổi của bản ghi đánh giá (kèm kiểm tra Concurrency Token).
    /// </summary>
    Task UpdateAsync(Review review, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra sự tồn tại của đánh giá cho một mục thuê theo chiều chỉ định.
    /// </summary>
    Task<bool> ExistsByItemAndDirectionAsync(Guid itemId, ReviewDirection direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra sự tồn tại của đánh giá cho một mục thuê theo chuỗi biểu diễn chiều.
    /// </summary>
    Task<bool> ExistsByItemAndDirectionAsync(Guid itemId, string direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm đánh giá theo mục thuê và chiều đánh giá.
    /// </summary>
    Task<Review?> GetByItemAndDirectionAsync(Guid itemId, ReviewDirection direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá công khai của một thiết bị kèm tổng số lượng (phân trang).
    /// Lọc: direction == renter_to_supplier_listing && authorState == Active && moderationState == Visible && equipmentListingId == listingId.
    /// Sắp xếp: CreatedAt DESC, Id DESC.
    /// </summary>
    Task<(IReadOnlyList<Review> Items, int TotalCount)> GetPublicListingReviewsAsync(
        Guid listingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá công khai của một nhà cung cấp kèm tổng số lượng (phân trang).
    /// Lọc: direction == renter_to_supplier_listing && authorState == Active && moderationState == Visible && subjectId == supplierId.
    /// Sắp xếp: CreatedAt DESC, Id DESC.
    /// </summary>
    Task<(IReadOnlyList<Review> Items, int TotalCount)> GetPublicSupplierReviewsAsync(
        Guid supplierId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá của chính tác giả kèm tổng số lượng (phân trang, bao gồm Active và Withdrawn).
    /// Lọc: authorId == authorId.
    /// Sắp xếp: CreatedAt DESC, Id DESC.
    /// </summary>
    Task<(IReadOnlyList<Review> Items, int TotalCount)> GetMyReviewsAsync(
        Guid authorId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tính toán tổng hợp điểm đánh giá cho thiết bị hoặc nhà cung cấp.
    /// TargetType 'listing': equipmentListingId == targetId. TargetType 'supplier': subjectId == targetId.
    /// Lọc: direction == renter_to_supplier_listing && authorState == Active && moderationState == Visible.
    /// </summary>
    Task<(int TotalCount, double? AverageRating, Dictionary<int, int> Distribution)> GetRatingSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá nhận được của một người dùng (active và visible).
    /// </summary>
    Task<(IReadOnlyList<Review> Items, int TotalCount)> GetReceivedReviewsAsync(
        Guid subjectId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tính tổng hợp điểm uy tín người thuê (supplier_to_renter) bằng server-side GROUP BY.
    /// </summary>
    Task<(int TotalCount, double? AverageRating, Dictionary<int, int> Distribution)> GetRenterReputationAsync(
        Guid renterId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách đánh giá của chính tác giả theo danh sách itemId (bỏ qua điều kiện public visibility, chỉ chiếu các trường cần thiết).
    /// </summary>
    Task<IReadOnlyList<ActorReviewSummary>> GetActorReviewsByItemIdsAsync(
        Guid authorId,
        IEnumerable<Guid> itemIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO rút gọn đại diện cho đánh giá do tác giả tạo phục vụ kiểm tra trạng thái đánh giá theo lô.
/// </summary>
public sealed record ActorReviewSummary(Guid ReviewId, Guid OrderId, Guid ItemId);
