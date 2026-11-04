using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.ReviewService.Application.Services;

/// <summary>
/// Triển khai use case duy nhất quản lý điều kiện đánh giá.
/// Đảm bảo tính nhất quán của mốc thời gian hoàn tất nghiệp vụ (completedAt) và tính toán chính xác hạn chót 30 ngày (deadlineAt).
/// Tuyệt đối không ghi đè mốc thời gian đã được xác thực trước đó.
/// </summary>
public sealed class ReviewEligibilityService : IReviewEligibilityService
{
    private readonly IReviewProjectionRepository _projectionRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewEligibilityService>? _logger;

    public ReviewEligibilityService(
        IReviewProjectionRepository projectionRepository,
        TimeProvider? timeProvider = null,
        ILogger<ReviewEligibilityService>? logger = null)
    {
        _projectionRepository = projectionRepository ?? throw new ArgumentNullException(nameof(projectionRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<Result> UpsertEligibilityAsync(RentalItemReviewSnapshotDto snapshot, CancellationToken ct = default)
    {
        if (snapshot == null)
        {
            return Result.Failure(Error.Validation("INVALID_SNAPSHOT", "Snapshot dữ liệu mục thuê không được rỗng."));
        }

        if (snapshot.ItemId == Guid.Empty || snapshot.OrderId == Guid.Empty)
        {
            return Result.Failure(Error.Validation("INVALID_SNAPSHOT_IDENTIFIERS", "ItemId và OrderId không được là Guid rỗng."));
        }

        if (!snapshot.CompletedAt.HasValue || snapshot.CompletedAt.Value == default)
        {
            _logger?.LogWarning("Snapshot mục thuê {ItemId} không có mốc thời gian hoàn tất hợp lệ.", snapshot.ItemId);
            return Result.Failure(Error.Validation("INVALID_COMPLETED_AT", "Mốc thời gian hoàn tất (completedAt) là bắt buộc và phải hợp lệ."));
        }

        DateTimeOffset completedAtUtc = snapshot.CompletedAt.Value.ToUniversalTime();
        DateTimeOffset deadlineAtUtc = completedAtUtc.AddDays(30);
        bool isCompleted = string.Equals(snapshot.Status, "Completed", StringComparison.OrdinalIgnoreCase);

        // Đủ điều kiện đánh giá khi trạng thái là Completed và có mốc completedAt hợp lệ
        bool isEligible = isCompleted;

        var existing = await _projectionRepository.GetByItemIdAsync(snapshot.ItemId, ct);
        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

        if (existing == null)
        {
            var projection = new ReviewProjection(
                Guid.NewGuid(),
                snapshot.OrderId,
                snapshot.ItemId,
                snapshot.RenterId,
                snapshot.SupplierId,
                snapshot.EquipmentListingId,
                completedAtUtc,
                deadlineAtUtc,
                isEligible,
                nowUtc);

            await _projectionRepository.UpsertAsync(projection, ct);
        }
        else
        {
            // Bảo vệ mốc thời gian: không âm thầm ghi đè timestamp đã chốt trước đó
            DateTimeOffset finalCompletedAt = existing.CompletedAt ?? completedAtUtc;
            DateTimeOffset finalDeadlineAt = existing.DeadlineAt ?? deadlineAtUtc;

            if (existing.CompletedAt.HasValue && existing.CompletedAt.Value != completedAtUtc)
            {
                _logger?.LogWarning(
                    "Phát hiện lệch dữ liệu timestamp mục thuê {ItemId}. Đã có: {Existing}, Mới: {New}. Giữ nguyên giá trị cũ đã chốt.",
                    snapshot.ItemId, existing.CompletedAt.Value, completedAtUtc);
            }

            existing.UpdateEligibility(
                snapshot.SupplierId,
                finalCompletedAt,
                finalDeadlineAt,
                isEligible,
                nowUtc);

            await _projectionRepository.UpsertAsync(existing, ct);
        }

        return Result.Success();
    }
}
