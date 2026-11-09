using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.DTOs.ReviewIntegration;
using Dprental.RentalService.Application.IServices.ReviewIntegration;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.ReviewIntegration;

/// <summary>
/// Triển khai IRentalReviewIntegrationService xử lý use case tích hợp ReviewService.
/// Nhận dữ liệu qua IRentalOrderRepository, quản lý validation, cursor, cửa sổ 30 ngày và mapping DTO.
/// </summary>
public sealed class RentalReviewIntegrationService : IRentalReviewIntegrationService
{
    private readonly IRentalOrderRepository _orderRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReviewIntegrationService>? _logger;

    public RentalReviewIntegrationService(
        IRentalOrderRepository orderRepository,
        TimeProvider? timeProvider = null,
        ILogger<RentalReviewIntegrationService>? logger = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty || itemId == Guid.Empty)
        {
            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Không tìm thấy mục thuê hoặc mục thuê không thuộc về đơn hàng đã chỉ định."));
        }

        var record = await _orderRepository.GetReviewSnapshotRecordAsync(itemId, cancellationToken);
        if (record == null)
        {
            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Không tìm thấy mục thuê hoặc mục thuê không thuộc về đơn hàng đã chỉ định."));
        }

        if (record.OrderId != orderId)
        {
            return Result.Failure<RentalItemReviewSnapshotDto>(
                Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Mục thuê không thuộc về đơn hàng đã chỉ định."));
        }

        return Result.Success(new RentalItemReviewSnapshotDto(
            record.OrderId,
            record.ItemId,
            record.RenterId,
            record.SupplierId,
            record.EquipmentListingId,
            record.Status.ToString(),
            record.CompletedAt,
            record.OrderVersion));
    }

    public async Task<Result<RentalReviewReconciliationBatchDto>> GetReconciliationBatchAsync(
        string? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset windowStart;
        DateTimeOffset? lastCompletedAt = null;
        Guid? lastItemId = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!ReviewReconciliationCursor.TryDecode(cursor, out var decoded) || decoded == null)
            {
                return Result.Failure<RentalReviewReconciliationBatchDto>(
                    Error.Validation("INVALID_CURSOR", "Cursor đối soát không đúng định dạng."));
            }

            windowStart = decoded.WindowStart;
            lastCompletedAt = decoded.LastCompletedAt;
            lastItemId = decoded.LastItemId;
        }
        else
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            windowStart = now.AddDays(-30);
        }

        int effectiveLimit = limit <= 0 ? 100 : Math.Min(limit, 500);

        var page = await _orderRepository.GetReviewReconciliationPageAsync(
            windowStart,
            lastCompletedAt,
            lastItemId,
            effectiveLimit,
            cancellationToken);

        string? nextCursor = null;
        if (page.HasMore && page.Items.Count > 0)
        {
            var lastItem = page.Items[^1];
            nextCursor = ReviewReconciliationCursor.Encode(new ReviewReconciliationCursor(
                windowStart,
                lastItem.CompletedAt!.Value,
                lastItem.ItemId));
        }

        var dtoItems = page.Items.Select(r => new RentalItemReviewSnapshotDto(
            r.OrderId,
            r.ItemId,
            r.RenterId,
            r.SupplierId,
            r.EquipmentListingId,
            r.Status.ToString(),
            r.CompletedAt,
            r.OrderVersion)).ToList();

        return Result.Success(new RentalReviewReconciliationBatchDto(dtoItems, nextCursor, page.HasMore));
    }
}
