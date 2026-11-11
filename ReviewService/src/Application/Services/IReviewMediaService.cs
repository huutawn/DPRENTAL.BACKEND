using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Hợp đồng điều phối các use case đính kèm và gỡ media của đánh giá.
/// </summary>
public interface IReviewMediaService
{
    /// <summary>
    /// Đính kèm media vào đánh giá đã tạo.
    /// </summary>
    Task<Result<ReviewMediaResponse>> AttachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string? mediaType,
        string ifMatch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gỡ media khỏi đánh giá. Trả về phiên bản đánh giá mới.
    /// </summary>
    Task<Result<long>> DetachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string ifMatch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Yêu cầu sinh URL đọc riêng tư ngắn hạn cho một media đính kèm sau khi kiểm tra visibility.
    /// </summary>
    Task<Result<Dprental.ReviewService.Application.DTOs.Media.MediaAccessDto>> GetMediaPrivateAccessAsync(
        Guid reviewId,
        Guid mediaId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách media đính kèm thuộc đánh giá (sau khi kiểm tra visibility).
    /// </summary>
    Task<Result<IReadOnlyList<ReviewMediaResponse>>> GetReviewMediaListAsync(
        Guid reviewId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default);
}
