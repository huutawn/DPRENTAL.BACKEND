using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Application.Validation;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Application service xử lý use cases đọc lịch, tạo khoảng chặn lịch và xóa khoảng chặn lịch cho tài sản.
/// </summary>
public sealed class CalendarService : ICalendarService
{
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly IEquipmentListingRepository _listingRepository;
    private readonly IInventoryWriteTransaction _writeTransaction;
    private readonly TimeProvider _timeProvider;

    public CalendarService(
        IInventoryAssetRepository assetRepository,
        IEquipmentListingRepository listingRepository,
        IInventoryWriteTransaction writeTransaction,
        TimeProvider? timeProvider = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Đọc lịch tổng hợp của tài sản trong cửa sổ 6 tháng tính từ ngày hôm nay tại Việt Nam.
    /// </summary>
    public async Task<Result<CalendarResponse>> GetCalendarAsync(
        Guid assetId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được truy cập lịch.");
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var today = BusinessDateTime.ToVietnamDate(nowUtc);
        var horizon = today.Year == 9999 && today.Month > 6 ? DateOnly.MaxValue : today.AddMonths(6);
        var asset = await _assetRepository.GetCalendarSnapshotAsync(assetId, today, horizon, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanAccessCalendar(caller, asset.Listing.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Bạn không có quyền truy cập lịch của tài sản này.");
        }

        var entries = new List<CalendarEntryResponse>();

        foreach (var block in asset.CalendarBlocks)
        {
            entries.Add(new CalendarEntryResponse
            {
                Id = block.Id,
                Type = "CalendarBlock",
                StartDate = block.StartDate,
                EndDate = block.EndDate,
                Reason = block.Reason.ToString(),
                Note = block.Notes,
                HoldExpiresAt = null
            });
        }

        foreach (var alloc in asset.Allocations)
        {
            if (!AssetAvailabilityPolicy.OccupiesCalendar(alloc, nowUtc))
            {
                continue;
            }

            entries.Add(new CalendarEntryResponse
            {
                Id = alloc.Id,
                Type = alloc.Status == AllocationStatus.Held ? "Held" : "Allocated",
                StartDate = alloc.RentalPeriod.StartDate,
                EndDate = alloc.RentalPeriod.EndDate,
                Reason = null,
                Note = null,
                HoldExpiresAt = alloc.Status == AllocationStatus.Held ? alloc.HoldExpiresAt : null
            });
        }

        var sortedEntries = entries
            .OrderBy(e => e.StartDate)
            .ThenBy(e => e.EndDate)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ThenBy(e => e.Id)
            .ToList();

        return new CalendarResponse
        {
            AssetId = asset.Id,
            StartDate = today,
            EndDate = horizon,
            Entries = sortedEntries
        };
    }

    /// <inheritdoc />
    public async Task<Result<ListingCalendarResponse>> GetListingCalendarAsync(
        Guid listingId,
        DateOnly? fromDate,
        DateOnly? toDate,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (caller.IsService || !caller.UserId.HasValue)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân mới có quyền truy cập lịch của Listing.");
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var today = BusinessDateTime.ToVietnamDate(nowUtc);

        DateOnly validStart;
        DateOnly validEnd;

        if (fromDate.HasValue || toDate.HasValue)
        {
            var dateValResult = AvailabilityDateValidator.ValidateDateWindow(fromDate, toDate, out validStart, out validEnd);
            if (dateValResult.IsFailure)
            {
                return dateValResult.Error;
            }
        }
        else
        {
            validStart = today;
            validEnd = today.Year == 9999 && today.Month > 6 ? DateOnly.MaxValue : today.AddMonths(6);
        }

        var snapshot = await _listingRepository.GetCalendarSnapshotAsync(listingId, validStart, validEnd, cancellationToken);
        if (snapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (!InventoryAccessEvaluator.CanAccessListingCalendar(caller, snapshot.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Bạn không có quyền truy cập lịch của Listing này.");
        }

        // Lấy clock đánh giá occupancy SAU KHI snapshot I/O tải xong
        var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var days = new List<ListingCalendarDayResponse>();
        var d = validStart;
        while (true)
        {
            int occupied = 0;
            int blocked = 0;
            int available = 0;
            int suspendedOccupied = 0;
            int suspendedBlocked = 0;

            foreach (var unit in snapshot.Assets)
            {
                bool isOccupied = unit.Allocations.Any(al =>
                    al.RentalPeriod.StartDate <= d &&
                    al.RentalPeriod.EndDate >= d &&
                    AssetAvailabilityPolicy.OccupiesCalendar(al, evaluationNowUtc));

                bool isBlocked = unit.CalendarBlocks.Any(cb =>
                    cb.StartDate <= d &&
                    cb.EndDate >= d);

                if (unit.AcceptingBookings)
                {
                    if (isOccupied) occupied++;
                    if (isBlocked) blocked++;
                    if (!isOccupied && !isBlocked) available++;
                }
                else
                {
                    if (isOccupied) suspendedOccupied++;
                    if (isBlocked) suspendedBlocked++;
                }
            }

            days.Add(new ListingCalendarDayResponse(
                d,
                occupied,
                blocked,
                available,
                suspendedOccupied,
                suspendedBlocked));

            if (d == validEnd)
            {
                break;
            }
            d = d.AddDays(1);
        }

        int totalQuantity = snapshot.Assets.Count(a => a.AcceptingBookings);

        return Result.Success(new ListingCalendarResponse(
            listingId,
            validStart,
            validEnd,
            totalQuantity,
            days));
    }

    /// <summary>
    /// Tạo khoảng chặn lịch mới trên tài sản dưới advisory lock của PostgreSQL và lưu cùng Outbox event.
    /// </summary>
    public async Task<Result<CalendarBlockResponse>> CreateCalendarBlockAsync(
        Guid assetId,
        CreateCalendarBlockRequest request,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được tạo khoảng chặn lịch.");
        }

        if (request.StartDate == default)
        {
            return Error.Validation("START_DATE_REQUIRED", "Ngày bắt đầu không được để trống.");
        }

        if (request.EndDate == default)
        {
            return Error.Validation("END_DATE_REQUIRED", "Ngày kết thúc không được để trống.");
        }

        return await _writeTransaction.ExecuteAsync(assetId, async (ctx, ct) =>
        {
            // 1. Đọc lại asset sau khi đã giành lock
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
            }

            // 2. Kiểm tra ownership
            if (!InventoryAccessEvaluator.CanAccessCalendar(caller, asset.Listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Bạn không có quyền tạo khoảng chặn lịch cho tài sản này.");
            }

            // 3. Lấy now và today sau khi đã giành advisory lock
            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
            var today = BusinessDateTime.ToVietnamDate(nowUtc);

            // 4. Validate và tạo thực thể CalendarBlock
            var blockId = Guid.NewGuid();
            var blockResult = CalendarBlock.Create(
                id: blockId,
                assetId: assetId,
                startDate: request.StartDate,
                endDate: request.EndDate,
                reason: request.Reason,
                createdBy: caller.UserId.Value.ToString("D"),
                now: nowUtc,
                today: today,
                notes: request.Note);

            if (blockResult.IsFailure)
            {
                return blockResult.Error;
            }

            var block = blockResult.Value;

            // 5. Đọc candidate allocations và overlapping blocks hiện có
            var overlappingBlocks = await ctx.CalendarBlocks.GetOverlappingBlocksAsync(
                assetId, request.StartDate, request.EndDate, ct);
            var candidateAllocations = await ctx.Allocations.GetActiveCandidatesAsync(
                assetId, request.StartDate, request.EndDate, ct);

            // 6. Kiểm tra overlap (không kiểm tra AcceptingBookings)
            var overlapResult = AssetAvailabilityPolicy.CheckCalendarOverlap(
                request.StartDate,
                request.EndDate,
                nowUtc,
                candidateAllocations,
                overlappingBlocks);

            if (overlapResult.IsFailure)
            {
                return overlapResult.Error;
            }

            // 7. Stage block
            ctx.CalendarBlocks.Add(block);

            // 8. Stage availability outbox message
            var outboxMessage = CreateAvailabilityChangedOutboxMessage(asset, nowUtc);
            ctx.StageOutbox(outboxMessage);

            // 9. Trả response DTO
            var response = new CalendarBlockResponse(
                Id: block.Id,
                AssetId: block.AssetId,
                StartDate: block.StartDate,
                EndDate: block.EndDate,
                Reason: block.Reason,
                Note: block.Notes,
                CreatedAt: block.CreatedAt);

            return Result.Success(response);
        }, cancellationToken);
    }

    /// <summary>
    /// Xóa khoảng chặn lịch trên tài sản dưới advisory lock của PostgreSQL và lưu cùng Outbox event.
    /// </summary>
    public async Task<Result> DeleteCalendarBlockAsync(
        Guid assetId,
        Guid blockId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được xóa khoảng chặn lịch.");
        }

        var deleteResult = await _writeTransaction.ExecuteAsync<bool>(assetId, async (ctx, ct) =>
        {
            // 1. Đọc lại asset sau khi đã giành lock
            var asset = await ctx.Assets.GetByIdAsync(assetId, ct);
            if (asset is null)
            {
                return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
            }

            // 2. Kiểm tra ownership
            if (!InventoryAccessEvaluator.CanAccessCalendar(caller, asset.Listing.OwnerUserId))
            {
                return Error.Forbidden("FORBIDDEN", "Bạn không có quyền xóa khoảng chặn lịch của tài sản này.");
            }

            // 3. Đọc block theo ID
            var block = await ctx.CalendarBlocks.GetByIdAsync(blockId, ct);
            if (block is null || block.AssetId != assetId)
            {
                return Error.NotFound("CALENDAR_BLOCK_NOT_FOUND", $"Khoảng chặn lịch {blockId} không tồn tại trên tài sản {assetId}.");
            }

            var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            // 4. Xóa vật lý
            ctx.CalendarBlocks.Remove(block);

            // 5. Stage availability outbox message
            var outboxMessage = CreateAvailabilityChangedOutboxMessage(asset, nowUtc);
            ctx.StageOutbox(outboxMessage);

            return Result.Success(true);
        }, cancellationToken);

        return deleteResult.IsSuccess ? Result.Success() : deleteResult.Error;
    }

    private static OutboxMessage CreateAvailabilityChangedOutboxMessage(InventoryAsset asset, DateTimeOffset nowUtc)
    {
        return InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(asset.Id, nowUtc);
    }
}
