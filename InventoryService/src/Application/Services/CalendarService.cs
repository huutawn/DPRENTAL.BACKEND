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
    private readonly Dprental.InventoryService.Application.Caching.IInventoryReadCache _cache;
    private readonly Dprental.InventoryService.Application.Caching.InventoryCacheOptions _cacheOptions;
    private readonly TimeProvider _timeProvider;

    public CalendarService(
        IInventoryAssetRepository assetRepository,
        IEquipmentListingRepository listingRepository,
        IInventoryWriteTransaction writeTransaction,
        TimeProvider? timeProvider = null)
        : this(assetRepository, listingRepository, writeTransaction, null, null, timeProvider)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public CalendarService(
        IInventoryAssetRepository assetRepository,
        IEquipmentListingRepository listingRepository,
        IInventoryWriteTransaction writeTransaction,
        Dprental.InventoryService.Application.Caching.IInventoryReadCache? cache,
        Microsoft.Extensions.Options.IOptions<Dprental.InventoryService.Application.Caching.InventoryCacheOptions>? cacheOptions,
        TimeProvider? timeProvider = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _writeTransaction = writeTransaction ?? throw new ArgumentNullException(nameof(writeTransaction));
        _cache = cache ?? Dprental.InventoryService.Application.Caching.NoOpInventoryReadCache.Instance;
        _cacheOptions = cacheOptions?.Value ?? new Dprental.InventoryService.Application.Caching.InventoryCacheOptions();
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
        string scope = $"asset:{assetId:D}";
        string queryType = "calendar";
        string inputHash = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var pipelineResult = await _cache.GetOrCreateAsync<CachedCalendarPayload>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var asset = await _assetRepository.GetCalendarSnapshotAsync(assetId, today, horizon, ct);
                if (asset is null)
                {
                    return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
                }

                var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
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
                    var displayInterval = AssetAvailabilityPolicy.GetDisplayOccupiedInterval(alloc, today, horizon, evaluationNowUtc);
                    if (!displayInterval.HasValue)
                    {
                        continue;
                    }

                    entries.Add(new CalendarEntryResponse
                    {
                        Id = alloc.Id,
                        Type = alloc.Status == AllocationStatus.Held ? "Held" : "Allocated",
                        StartDate = displayInterval.Value.StartDate,
                        EndDate = displayInterval.Value.EndDate,
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

                var response = new CalendarResponse
                {
                    AssetId = asset.Id,
                    StartDate = today,
                    EndDate = horizon,
                    Entries = sortedEntries
                };

                var baseTtl = TimeSpan.FromSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                DateTimeOffset? nearestHold = asset.Allocations
                    .Where(a => a.Status == AllocationStatus.Held && a.HoldExpiresAt.HasValue && a.HoldExpiresAt.Value > evaluationNowUtc)
                    .Select(a => a.HoldExpiresAt)
                    .OrderBy(t => t!.Value)
                    .FirstOrDefault();

                var deadline = Dprental.InventoryService.Application.Caching.CacheKeyHelper.CalculateOccupancyDeadline(
                    evaluationNowUtc.Add(baseTtl),
                    nearestHold,
                    asset.Allocations,
                    evaluationNowUtc);

                DateTimeOffset? finalDeadline = deadline > evaluationNowUtc ? deadline : null;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CachedCalendarPayload>(
                    new CachedCalendarPayload(asset.Listing.OwnerUserId, response),
                    finalDeadline));
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var payload = pipelineResult.Value;
        if (!InventoryAccessEvaluator.CanAccessCalendar(caller, payload.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Bạn không có quyền truy cập lịch của tài sản này.");
        }

        return payload.Response;
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

        string listingScope = $"listing:{listingId:D}";
        string listingQueryType = "listing-calendar";
        string listingInputHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputeWindowHash(validStart, validEnd);

        var pipelineResult = await _cache.GetOrCreateAsync<CachedListingCalendarPayload>(
            listingScope,
            listingQueryType,
            listingInputHash,
            async (genToken, ct) =>
            {
                var snapshot = await _listingRepository.GetCalendarSnapshotAsync(listingId, validStart, validEnd, ct);
                if (snapshot is null)
                {
                    return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
                }

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
                        {
                            var interval = AssetAvailabilityPolicy.GetOccupiedInterval(al, evaluationNowUtc);
                            return interval.HasValue && interval.Value.StartDate <= d && interval.Value.EndDate >= d;
                        });

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
                var response = new ListingCalendarResponse(
                    listingId,
                    validStart,
                    validEnd,
                    totalQuantity,
                    days);

                var baseTtl = TimeSpan.FromSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                var allAllocations = snapshot.Assets.SelectMany(a => a.Allocations);
                DateTimeOffset? nearestHold = allAllocations
                    .Where(al => al.Status == AllocationStatus.Held && al.HoldExpiresAt.HasValue && al.HoldExpiresAt.Value > evaluationNowUtc)
                    .Select(al => al.HoldExpiresAt)
                    .OrderBy(t => t!.Value)
                    .FirstOrDefault();

                var deadline = Dprental.InventoryService.Application.Caching.CacheKeyHelper.CalculateOccupancyDeadline(
                    evaluationNowUtc.Add(baseTtl),
                    nearestHold,
                    allAllocations,
                    evaluationNowUtc);

                DateTimeOffset? finalDeadline = deadline > evaluationNowUtc ? deadline : null;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CachedListingCalendarPayload>(
                    new CachedListingCalendarPayload(snapshot.OwnerUserId, response),
                    finalDeadline));
            },
            cancellationToken);

        if (pipelineResult.IsFailure)
        {
            return pipelineResult.Error;
        }

        var payload = pipelineResult.Value;
        if (!InventoryAccessEvaluator.CanAccessListingCalendar(caller, payload.OwnerUserId))
        {
            return Error.Forbidden("FORBIDDEN", "Bạn không có quyền truy cập lịch của Listing này.");
        }

        return Result.Success(payload.Response);
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
            ctx.StageCacheInvalidation([$"asset:{asset.Id:D}", $"listing:{asset.EquipmentListingId:D}", "public-discovery"]);

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
            ctx.StageCacheInvalidation([$"asset:{asset.Id:D}", $"listing:{asset.EquipmentListingId:D}", "public-discovery"]);

            return Result.Success(true);
        }, cancellationToken);

        return deleteResult.IsSuccess ? Result.Success() : deleteResult.Error;
    }

    private static OutboxMessage CreateAvailabilityChangedOutboxMessage(InventoryAsset asset, DateTimeOffset nowUtc)
    {
        return InventoryAssetAvailabilityEvents.CreateAvailabilityChangedOutboxMessage(asset.Id, nowUtc);
    }

    internal sealed record CachedCalendarPayload(
        Guid OwnerUserId,
        CalendarResponse Response
    );

    internal sealed record CachedListingCalendarPayload(
        Guid OwnerUserId,
        ListingCalendarResponse Response
    );
}
