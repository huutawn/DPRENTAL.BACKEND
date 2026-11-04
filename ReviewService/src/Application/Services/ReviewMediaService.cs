using System.Globalization;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Dịch vụ quản lý liên kết media của đánh giá (Attach, Detach) thông qua IUnitOfWork và IReviewMediaRepository.
/// </summary>
public sealed class ReviewMediaService : IReviewMediaService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IReviewRepository _reviewRepository;
    private readonly IReviewMediaRepository _mediaRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewMediaService>? _logger;

    public ReviewMediaService(
        IUnitOfWork unitOfWork,
        IReviewRepository reviewRepository,
        IReviewMediaRepository mediaRepository,
        TimeProvider? timeProvider = null,
        ILogger<ReviewMediaService>? logger = null)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _reviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _mediaRepository = mediaRepository ?? throw new ArgumentNullException(nameof(mediaRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ReviewMediaResponse>> AttachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string? mediaType,
        string ifMatch,
        CancellationToken cancellationToken = default)
    {
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewMediaResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá."));
        }

        if (review.AuthorId != actorId)
        {
            return Result.Failure<ReviewMediaResponse>(
                Error.Forbidden("FORBIDDEN", "Chỉ tác giả mới có quyền đính kèm media vào đánh giá."));
        }

        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure<ReviewMediaResponse>(ifMatchError!);
        }

        int mediaCount = await _mediaRepository.CountByReviewIdAsync(reviewId, cancellationToken);
        if (mediaCount >= 5)
        {
            return Result.Failure<ReviewMediaResponse>(
                Error.Validation("MAX_MEDIA_EXCEEDED", "Mỗi đánh giá chỉ được đính kèm tối đa 5 hình ảnh."));
        }

        var existing = await _mediaRepository.GetByReviewAndMediaIdAsync(reviewId, mediaId, cancellationToken);
        if (existing != null)
        {
            return Result.Failure<ReviewMediaResponse>(
                Error.Conflict("MEDIA_ALREADY_ATTACHED", "Media này đã được đính kèm vào đánh giá."));
        }

        var now = _timeProvider.GetUtcNow();
        var media = new ReviewMedia(Guid.NewGuid(), reviewId, mediaId, mediaType ?? "image", now);
        review.Version++;

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            await _mediaRepository.AddAsync(media, cancellationToken);
            await _reviewRepository.UpdateAsync(review, cancellationToken);
            await _unitOfWork.CommitAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _unitOfWork.DiscardChanges();
            _logger?.LogError(ex, "Lỗi khi đính kèm media {MediaId} vào review {ReviewId}", mediaId, reviewId);
            throw;
        }

        return Result.Success(new ReviewMediaResponse(media.Id, media.ReviewId, media.MediaId, media.MediaType, media.CreatedAt));
    }

    /// <inheritdoc />
    public async Task<Result> DetachMediaAsync(
        Guid reviewId,
        Guid actorId,
        Guid mediaId,
        string ifMatch,
        CancellationToken cancellationToken = default)
    {
        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure(Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá."));
        }

        if (review.AuthorId != actorId)
        {
            return Result.Failure(Error.Forbidden("FORBIDDEN", "Chỉ tác giả mới có quyền gỡ media khỏi đánh giá."));
        }

        if (!ParseIfMatch(ifMatch, review.Version, out var ifMatchError))
        {
            return Result.Failure(ifMatchError!);
        }

        var media = await _mediaRepository.GetByReviewAndMediaIdAsync(reviewId, mediaId, cancellationToken);
        if (media != null)
        {
            review.Version++;

            await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);
            try
            {
                _mediaRepository.Remove(media);
                await _reviewRepository.UpdateAsync(review, cancellationToken);
                await _unitOfWork.CommitAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(cancellationToken);
                _unitOfWork.DiscardChanges();
                _logger?.LogError(ex, "Lỗi khi gỡ media {MediaId} khỏi review {ReviewId}", mediaId, reviewId);
                throw;
            }
        }

        return Result.Success();
    }

    private static bool ParseIfMatch(string? ifMatch, long currentVersion, out Error? error)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            error = Error.PreconditionRequired("PRECONDITION_REQUIRED", "Header If-Match chứa ETag phiên bản là bắt buộc đối với thao tác này.");
            return false;
        }
        string cleaned = ifMatch.Trim();

        if (cleaned == "*")
        {
            error = Error.Validation("INVALID_IF_MATCH", "Header If-Match không được sử dụng ký tự đại diện (*).");
            return false;
        }

        if (cleaned.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            error = Error.Validation("WEAK_ETAG_NOT_SUPPORTED", "Hệ thống chỉ chấp nhận Strong ETag dạng số nguyên phiên bản, không chấp nhận Weak ETag (W/).");
            return false;
        }

        cleaned = cleaned.Trim('"');

        if (!long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion) || parsedVersion <= 0)
        {
            error = Error.Validation("INVALID_IF_MATCH", "Giá trị If-Match không hợp lệ. Phải là số nguyên phiên bản dương trong dấu ngoặc kép.");
            return false;
        }

        if (parsedVersion != currentVersion)
        {
            error = Error.PreconditionFailed(
                "ETAG_PRECONDITION_FAILED",
                $"Phiên bản đánh giá không khớp. Phiên bản cung cấp là {parsedVersion}, phiên bản hiện tại là {currentVersion}.");
            return false;
        }

        error = null;
        return true;
    }
}
