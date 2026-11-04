using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Dịch vụ xử lý các truy vấn đánh giá (Public, Author, Reputation, Summary, Pending) thông qua repository contracts.
/// </summary>
public sealed class ReviewQueryService : IReviewQueryService
{
    private readonly IReviewRepository _reviewRepository;
    private readonly IReviewProjectionRepository _projectionRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewQueryService>? _logger;

    public ReviewQueryService(
        IReviewRepository reviewRepository,
        IReviewProjectionRepository projectionRepository,
        TimeProvider? timeProvider = null,
        ILogger<ReviewQueryService>? logger = null)
    {
        _reviewRepository = reviewRepository ?? throw new ArgumentNullException(nameof(reviewRepository));
        _projectionRepository = projectionRepository ?? throw new ArgumentNullException(nameof(projectionRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ReviewDetailResponse>> GetReviewDetailAsync(
        Guid reviewId,
        ReviewActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.ActorId == Guid.Empty)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa được xác thực."));
        }

        var review = await _reviewRepository.GetByIdAsync(reviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        // Quyền riêng tư: Chỉ Author, Subject, hoặc Admin mới được xem
        bool isAuthor = actor.ActorId == review.AuthorId;
        bool isSubject = actor.ActorId == review.SubjectId;

        if (!isAuthor && !isSubject && !actor.IsAdmin)
        {
            // Trả về 404 REVIEW_NOT_FOUND để không tiết lộ sự tồn tại của review
            return Result.Failure<ReviewDetailResponse>(
                Error.NotFound("REVIEW_NOT_FOUND", "Không tìm thấy đánh giá đã chỉ định."));
        }

        return Result.Success(ReviewDetailResponse.FromEntity(review));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicReviewItemResponse>>> GetListingReviewsAsync(
        Guid listingId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (listingId == Guid.Empty)
        {
            return Result.Failure<PagedResult<PublicReviewItemResponse>>(
                Error.Validation("INVALID_LISTING_ID", "Mã thiết bị không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetPublicListingReviewsAsync(
            listingId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(r => PublicReviewItemResponse.FromEntity(r))
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<PublicReviewItemResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PublicReviewItemResponse>>> GetSupplierReviewsAsync(
        Guid supplierId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (supplierId == Guid.Empty)
        {
            return Result.Failure<PagedResult<PublicReviewItemResponse>>(
                Error.Validation("INVALID_SUPPLIER_ID", "Mã nhà cung cấp không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetPublicSupplierReviewsAsync(
            supplierId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(r => PublicReviewItemResponse.FromEntity(r))
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<PublicReviewItemResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty)
        {
            return Result.Failure<PagedResult<ReviewDetailResponse>>(
                Error.Unauthorized("UNAUTHORIZED", "Mã người dùng không hợp lệ."));
        }

        var safePage = Math.Max(1, page);
        var safePageSize = pageSize < 1 ? 20 : Math.Min(pageSize, 50);

        var (items, totalCount) = await _reviewRepository.GetMyReviewsAsync(
            actorId,
            safePage,
            safePageSize,
            cancellationToken);

        var mappedItems = items
            .Select(ReviewDetailResponse.FromEntity)
            .ToList();

        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return Result.Success(new PagedResult<ReviewDetailResponse>(
            mappedItems,
            safePage,
            safePageSize,
            totalCount,
            totalPages));
    }

    /// <inheritdoc />
    public Task<Result<PagedResult<ReviewDetailResponse>>> GetMyReviewsAsync(
        ReviewActorContext actor,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return GetMyReviewsAsync(actor.ActorId, page, pageSize, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ReviewSummaryResponse>> GetSummaryAsync(
        string targetType,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        var isListing = string.Equals(targetType, "listing", StringComparison.OrdinalIgnoreCase);
        var isSupplier = string.Equals(targetType, "supplier", StringComparison.OrdinalIgnoreCase);

        if (!isListing && !isSupplier)
        {
            return Result.Failure<ReviewSummaryResponse>(
                Error.Validation(
                    "INVALID_TARGET_TYPE",
                    $"Loại đối tượng không hợp lệ: '{targetType}'. Chỉ chấp nhận 'listing' hoặc 'supplier'."));
        }

        if (targetId == Guid.Empty)
        {
            return Result.Failure<ReviewSummaryResponse>(
                Error.Validation("INVALID_TARGET_ID", "Mã đối tượng không hợp lệ."));
        }

        var normalizedTargetType = isListing ? "listing" : "supplier";

        var (totalCount, averageRating, distribution) = await _reviewRepository.GetRatingSummaryAsync(
            normalizedTargetType,
            targetId,
            cancellationToken);

        var stringDistribution = new Dictionary<string, int>
        {
            ["5"] = distribution != null && distribution.TryGetValue(5, out var c5) ? c5 : 0,
            ["4"] = distribution != null && distribution.TryGetValue(4, out var c4) ? c4 : 0,
            ["3"] = distribution != null && distribution.TryGetValue(3, out var c3) ? c3 : 0,
            ["2"] = distribution != null && distribution.TryGetValue(2, out var c2) ? c2 : 0,
            ["1"] = distribution != null && distribution.TryGetValue(1, out var c1) ? c1 : 0
        };

        var response = new ReviewSummaryResponse(
            normalizedTargetType,
            targetId,
            totalCount == 0 ? null : averageRating,
            totalCount,
            stringDistribution);

        return Result.Success(response);
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<PendingReviewItemResponse>>> GetPendingReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty)
        {
            return Result.Failure<PagedResult<PendingReviewItemResponse>>(
                Error.Unauthorized("UNAUTHORIZED", "Xác thực không hợp lệ."));
        }

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 50) pageSize = 50;

        var nowUtc = _timeProvider.GetUtcNow();

        var (items, totalCount) = await _projectionRepository.GetPendingEligibleItemsAsync(
            actorId,
            nowUtc,
            page,
            pageSize,
            cancellationToken);

        var responses = items.Select(p =>
        {
            var isRenter = p.RenterId == actorId;
            var counterpartId = isRenter ? p.SupplierId : p.RenterId;
            var dirStr = isRenter ? "renter_to_supplier_listing" : "supplier_to_renter";
            var daysRemaining = (int)Math.Max(0, Math.Ceiling((p.DeadlineAt!.Value - nowUtc).TotalDays));

            return new PendingReviewItemResponse(
                OrderId: p.OrderId,
                ItemId: p.ItemId,
                EquipmentListingId: p.EquipmentListingId,
                CounterpartId: counterpartId,
                Direction: dirStr,
                CompletedAt: p.CompletedAt!.Value,
                DeadlineAt: p.DeadlineAt!.Value,
                DaysRemaining: daysRemaining);
        }).ToList();

        return Result.Success(PagedResult<PendingReviewItemResponse>.Create(
            items: responses,
            page: page,
            pageSize: pageSize,
            totalCount: totalCount));
    }

    /// <inheritdoc />
    public async Task<Result<PagedResult<ReviewDetailResponse>>> GetReceivedReviewsAsync(
        Guid actorId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (actorId == Guid.Empty)
        {
            return Result.Failure<PagedResult<ReviewDetailResponse>>(
                Error.Unauthorized("UNAUTHORIZED", "Xác thực không hợp lệ."));
        }

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 50) pageSize = 50;

        var (items, totalCount) = await _reviewRepository.GetReceivedReviewsAsync(
            actorId,
            page,
            pageSize,
            cancellationToken);

        var responses = items.Select(r => new ReviewDetailResponse(
            ReviewId: r.Id,
            OrderId: r.OrderId,
            ItemId: r.ItemId,
            Direction: r.Direction == ReviewDirection.supplier_to_renter ? "supplier_to_renter" : "renter_to_supplier_listing",
            AuthorId: r.AuthorId,
            SubjectId: r.SubjectId,
            EquipmentListingId: r.EquipmentListingId,
            Rating: r.Rating,
            Comment: r.Comment,
            AuthorState: r.AuthorState.ToString(),
            ModerationState: r.ModerationState.ToString(),
            Version: r.Version,
            CreatedAt: r.CreatedAt,
            UpdatedAt: r.UpdatedAt,
            WithdrawnAt: r.WithdrawnAt,
            CanEditUntil: r.CanEditUntil)).ToList();

        return Result.Success(PagedResult<ReviewDetailResponse>.Create(
            items: responses,
            page: page,
            pageSize: pageSize,
            totalCount: totalCount));
    }

    /// <inheritdoc />
    public async Task<Result<RenterReputationResponse>> GetRenterReputationAsync(
        Guid renterId,
        CancellationToken cancellationToken = default)
    {
        if (renterId == Guid.Empty)
        {
            return Result.Failure<RenterReputationResponse>(
                Error.Unauthorized("UNAUTHORIZED", "Xác thực không hợp lệ."));
        }

        var (count, avg, dist) = await _reviewRepository.GetRenterReputationAsync(renterId, cancellationToken);

        var stringDistribution = new Dictionary<string, int>
        {
            ["5"] = dist.TryGetValue(5, out var c5) ? c5 : 0,
            ["4"] = dist.TryGetValue(4, out var c4) ? c4 : 0,
            ["3"] = dist.TryGetValue(3, out var c3) ? c3 : 0,
            ["2"] = dist.TryGetValue(2, out var c2) ? c2 : 0,
            ["1"] = dist.TryGetValue(1, out var c1) ? c1 : 0
        };

        return Result.Success(new RenterReputationResponse(renterId, avg, count, stringDistribution));
    }
}
