using Dprental.InventoryService.Application.Caching;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Application service thực thi use cases kiểm tra tính khả dụng (Availability Check)
/// và tính toán các khoảng bận đã làm sạch (Busy Ranges) theo DPR-150.
/// Tích hợp cấu hình tập trung, đánh giá occupancy với clock sau I/O, và cơ chế tự phục hồi khi cache hết hạn giữa chừng.
/// </summary>
public sealed class InventoryAvailabilityService : IInventoryAvailabilityService
{
    private readonly IInventoryAssetRepository _assetRepository;
    private readonly IEquipmentListingRepository _listingRepository;
    private readonly IInventoryReadCache _cache;
    private readonly InventoryCacheOptions _cacheOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryAvailabilityService>? _logger;
    public InventoryAvailabilityService(
        IInventoryAssetRepository assetRepository,
        IEquipmentListingRepository listingRepository,
        IInventoryReadCache cache,
        TimeProvider timeProvider)
        : this(assetRepository, listingRepository, cache, null, timeProvider, null)
    {
    }

    public InventoryAvailabilityService(
        IInventoryAssetRepository assetRepository,
        IEquipmentListingRepository listingRepository,
        IInventoryReadCache cache,
        IOptions<InventoryCacheOptions>? cacheOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<InventoryAvailabilityService>? logger = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _listingRepository = listingRepository ?? throw new ArgumentNullException(nameof(listingRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _cacheOptions = cacheOptions?.Value ?? new InventoryCacheOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }
    public async Task<Result<CheckAvailabilityBatchResponse>> CheckAvailabilityBatchAsync(
        CheckAvailabilityBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return Error.Validation("INVALID_REQUEST", "Yêu cầu không được để trống.");
        }

        if (request.AssetIds is null || request.AssetIds.Count == 0)
        {
            return Error.Validation("EMPTY_ASSET_IDS", "Danh sách assetIds bắt buộc và không được rỗng.");
        }

        if (request.AssetIds.Count > 500)
        {
            return Error.Validation("EXCEEDED_ASSET_IDS_LIMIT", "Danh sách assetIds không được vượt quá 500 phần tử trước khi gộp trùng.");
        }

        if (request.AssetIds.Any(id => id == Guid.Empty))
        {
            return Error.Validation("INVALID_ASSET_ID", "Danh sách assetIds chứa GUID rỗng không hợp lệ.");
        }

        var dateValidation = AvailabilityDateValidator.ValidateDateWindow(
            request.StartDate,
            request.EndDate,
            out var validStart,
            out var validEnd);

        if (dateValidation.IsFailure)
        {
            return dateValidation.Error;
        }

        var deduplicatedIds = request.AssetIds.Distinct().ToList();

        var periodResult = RentalPeriod.Create(validStart, validEnd);
        if (periodResult.IsFailure)
        {
            return periodResult.Error;
        }

        string dateHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputeWindowHash(validStart, validEnd);
        var targets = deduplicatedIds
            .Select(id => new Dprental.InventoryService.Application.Caching.CacheTarget($"asset:{id:D}", "check-avail", dateHash))
            .ToList();

        var bulkResult = await _cache.GetOrCreateBulkAsync<AssetAvailabilityCheckCachedDto>(
            targets,
            async (stillMissing, ct) =>
            {
                var missingIds = stillMissing
                    .Select(t => Guid.Parse(t.Scope["asset:".Length..]))
                    .Distinct()
                    .ToList();

                var snapshots = await _assetRepository.GetAvailabilitySnapshotsAsync(
                    missingIds,
                    validStart,
                    validEnd,
                    ct);

                var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
                var assetsById = snapshots.ToDictionary(a => a.Id);
                var produced = new Dictionary<Dprental.InventoryService.Application.Caching.CacheTarget, Dprental.InventoryService.Application.Caching.CachedRead<AssetAvailabilityCheckCachedDto>>();

                foreach (var target in stillMissing)
                {
                    var id = Guid.Parse(target.Scope["asset:".Length..]);
                    if (!assetsById.TryGetValue(id, out var asset))
                    {
                        produced[target] = new Dprental.InventoryService.Application.Caching.CachedRead<AssetAvailabilityCheckCachedDto>(
                            new AssetAvailabilityCheckCachedDto(false, true),
                            null);
                        continue;
                    }

                    var checkResult = AssetAvailabilityPolicy.CheckAvailability(
                        asset,
                        periodResult.Value,
                        evaluationNowUtc,
                        asset.Allocations,
                        asset.CalendarBlocks);

                    bool isAvail = checkResult.IsSuccess;
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
                    produced[target] = new Dprental.InventoryService.Application.Caching.CachedRead<AssetAvailabilityCheckCachedDto>(
                        new AssetAvailabilityCheckCachedDto(isAvail, false),
                        finalDeadline);
                }

                return Result.Success<IReadOnlyDictionary<Dprental.InventoryService.Application.Caching.CacheTarget, Dprental.InventoryService.Application.Caching.CachedRead<AssetAvailabilityCheckCachedDto>>>(produced);
            },
            cancellationToken);

        if (bulkResult.IsFailure)
        {
            return bulkResult.Error;
        }

        var unavailableAssetIds = new List<Guid>();
        var notFoundAssetIds = new List<Guid>();

        foreach (var id in deduplicatedIds)
        {
            var target = new Dprental.InventoryService.Application.Caching.CacheTarget($"asset:{id:D}", "check-avail", dateHash);
            if (bulkResult.Value.TryGetValue(target, out var dto))
            {
                if (dto.IsNotFound)
                {
                    notFoundAssetIds.Add(id);
                }
                else if (!dto.IsAvailable)
                {
                    unavailableAssetIds.Add(id);
                }
            }
        }

        return Result.Success(new CheckAvailabilityBatchResponse(
            unavailableAssetIds,
            notFoundAssetIds));
    }

    /// <inheritdoc />
    public async Task<Result<CheckListingAvailabilityResponse>> CheckListingAvailabilityAsync(
        Guid listingId,
        CheckListingAvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (listingId == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        if (request.RequestedQuantity < 1)
        {
            return Error.Validation("INVALID_REQUESTED_QUANTITY", "Số lượng yêu cầu (requestedQuantity) phải lớn hơn hoặc bằng 1.");
        }

        var dateValResult = AvailabilityDateValidator.ValidateDateWindow(
            request.StartDate,
            request.EndDate,
            out var validStart,
            out var validEnd);

        if (dateValResult.IsFailure)
        {
            return dateValResult.Error;
        }

        string listingScope = $"listing:{listingId:D}";
        string listingQueryType = "check-avail";
        string listingInputHash = Dprental.InventoryService.Application.Caching.CacheKeyHelper.ComputeWindowHash(validStart, validEnd, request.RequestedQuantity);

        return await _cache.GetOrCreateAsync<CheckListingAvailabilityResponse>(
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

                if (snapshot.Status != EquipmentListingStatus.Active)
                {
                    var inactiveResponse = new CheckListingAvailabilityResponse(
                        listingId,
                        validStart,
                        validEnd,
                        request.RequestedQuantity,
                        AvailableQuantity: 0,
                        IsAvailable: false);
                    var ttlInactive = evaluationNowUtc.AddSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                    return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CheckListingAvailabilityResponse>(inactiveResponse, ttlInactive));
                }

                int availableQuantity = snapshot.Assets.Count(a =>
                    a.AcceptingBookings &&
                    !a.CalendarBlocks.Any(cb => cb.StartDate <= validEnd && cb.EndDate >= validStart) &&
                    !a.Allocations.Any(al =>
                        (al.Status == AllocationStatus.Allocated ||
                         (al.Status == AllocationStatus.Held && al.HoldExpiresAt > evaluationNowUtc)) &&
                        al.RentalPeriod.StartDate <= validEnd &&
                        al.RentalPeriod.EndDate >= validStart));

                bool isAvailable = availableQuantity >= request.RequestedQuantity;

                var response = new CheckListingAvailabilityResponse(
                    listingId,
                    validStart,
                    validEnd,
                    request.RequestedQuantity,
                    availableQuantity,
                    isAvailable);

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
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<CheckListingAvailabilityResponse>(response, finalDeadline));
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<AssetBusyRangesResponse>> GetBusyRangesAsync(
        Guid assetId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Định danh tài sản không hợp lệ.");
        }

        var dateValidation = AvailabilityDateValidator.ValidateDateWindow(
            fromDate,
            toDate,
            out var validFrom,
            out var validTo);

        if (dateValidation.IsFailure)
        {
            return dateValidation.Error;
        }

        // Busy-ranges luôn đọc existence và AcceptingBookings từ PostgreSQL kể cả cache hit
        var asset = await _assetRepository.GetByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return Error.NotFound("ASSET_NOT_FOUND", $"Tài sản với ID {assetId} không tồn tại.");
        }

        var acceptingBookings = asset.AcceptingBookings;

        var months = BuildMonthList(validFrom, validTo);
        var targets = months
            .Select(m => new Dprental.InventoryService.Application.Caching.CacheTarget($"asset:{assetId:D}", "busy", m.Key))
            .ToList();

        var bulkResult = await _cache.GetOrCreateBulkAsync<CachedMonthRanges>(
            targets,
            async (stillMissing, ct) =>
            {
                var missingMonthKeys = stillMissing.Select(t => t.InputHash).ToHashSet(StringComparer.Ordinal);
                var missingMonthBounds = months.Where(m => missingMonthKeys.Contains(m.Key)).ToList();

                var minMissingDate = missingMonthBounds.Min(m => m.MonthStart);
                var maxMissingDate = missingMonthBounds.Max(m => m.MonthEnd);

                var snapshots = await _assetRepository.GetAvailabilitySnapshotsAsync(
                    new[] { assetId },
                    minMissingDate,
                    maxMissingDate,
                    ct);

                var postSnapshotNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
                var snapshotAsset = snapshots.FirstOrDefault(a => a.Id == assetId);

                var produced = new Dictionary<Dprental.InventoryService.Application.Caching.CacheTarget, Dprental.InventoryService.Application.Caching.CachedRead<CachedMonthRanges>>();
                var baseTtlSec = _cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15;

                foreach (var target in stillMissing)
                {
                    var m = missingMonthBounds.First(mb => mb.Key == target.InputHash);
                    var (rangesForMonth, nearestHoldExpiry) = CalculateMonthOccupants(snapshotAsset, m.MonthStart, m.MonthEnd, postSnapshotNow);

                    var deadline = Dprental.InventoryService.Application.Caching.CacheKeyHelper.CalculateOccupancyDeadline(
                        postSnapshotNow.AddSeconds(baseTtlSec),
                        nearestHoldExpiry,
                        snapshotAsset?.Allocations,
                        postSnapshotNow);

                    DateTimeOffset? finalDeadline = deadline > postSnapshotNow ? deadline : null;
                    var cachedData = new CachedMonthRanges(rangesForMonth, deadline);
                    produced[target] = new Dprental.InventoryService.Application.Caching.CachedRead<CachedMonthRanges>(cachedData, finalDeadline);
                }

                return Result.Success<IReadOnlyDictionary<Dprental.InventoryService.Application.Caching.CacheTarget, Dprental.InventoryService.Application.Caching.CachedRead<CachedMonthRanges>>>(produced);
            },
            cancellationToken);

        if (bulkResult.IsFailure)
        {
            return bulkResult.Error;
        }

        var monthRanges = bulkResult.Value.ToDictionary(kv => kv.Key.InputHash, kv => (kv.Value.Ranges, kv.Value.ExpiresAtUtc));
        // Kiểm tra lại deadline trước khi trả response
        var preReturnNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        bool anyHitExpiredDuringProcessing = monthRanges.Values.Any(v => v.ExpiresAtUtc <= preReturnNow);

        if (anyHitExpiredDuringProcessing)
        {
            // Nếu có cached month hết hạn trong quá trình xử lý: đọc lại toàn cửa sổ từ PostgreSQL một lần bằng clock mới
            var reloadSnapshots = await _assetRepository.GetAvailabilitySnapshotsAsync(
                new[] { assetId },
                validFrom,
                validTo,
                cancellationToken);

            var reloadNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
            var reloadAsset = reloadSnapshots.FirstOrDefault(a => a.Id == assetId);
            var reloadedRanges = CalculateRangeOccupants(reloadAsset, validFrom, validTo, reloadNow);

            return Result.Success(new AssetBusyRangesResponse(
                assetId,
                acceptingBookings,
                reloadedRanges));
        }

        // Hợp nhất, clip vào [validFrom, validTo], sort và merge overlap / liền nhau
        var allClipped = new List<(DateOnly Start, DateOnly End)>();
        foreach (var kvp in monthRanges)
        {
            foreach (var r in kvp.Value.Ranges)
            {
                if (r.StartDate <= validTo && r.EndDate >= validFrom)
                {
                    var clippedStart = r.StartDate < validFrom ? validFrom : r.StartDate;
                    var clippedEnd = r.EndDate > validTo ? validTo : r.EndDate;
                    allClipped.Add((clippedStart, clippedEnd));
                }
            }
        }

        var finalRanges = MergeRanges(allClipped);

        return Result.Success(new AssetBusyRangesResponse(
            assetId,
            acceptingBookings,
            finalRanges));
    }

    private static List<(string Key, DateOnly MonthStart, DateOnly MonthEnd)> BuildMonthList(DateOnly from, DateOnly to)
    {
        var result = new List<(string Key, DateOnly MonthStart, DateOnly MonthEnd)>();
        int year = from.Year;
        int month = from.Month;

        while (true)
        {
            var key = $"{year:D4}{month:D2}";
            int daysInMonth = DateTime.DaysInMonth(year, month);
            var mStart = new DateOnly(year, month, 1);
            var mEnd = new DateOnly(year, month, daysInMonth);

            result.Add((key, mStart, mEnd));

            if (year == to.Year && month == to.Month)
            {
                break;
            }

            if (month == 12)
            {
                year++;
                month = 1;
            }
            else
            {
                month++;
            }
        }

        return result;
    }

    private static (IReadOnlyList<BusyRangeDto> Ranges, DateTimeOffset? NearestHoldExpiry) CalculateMonthOccupants(
        InventoryAsset? asset,
        DateOnly monthStart,
        DateOnly monthEnd,
        DateTimeOffset nowUtc)
    {
        if (asset is null)
        {
            return (Array.Empty<BusyRangeDto>(), null);
        }

        var rawRanges = new List<(DateOnly Start, DateOnly End)>();
        DateTimeOffset? nearestHoldExpiry = null;

        foreach (var block in asset.CalendarBlocks)
        {
            if (block.StartDate <= monthEnd && block.EndDate >= monthStart)
            {
                var s = block.StartDate < monthStart ? monthStart : block.StartDate;
                var e = block.EndDate > monthEnd ? monthEnd : block.EndDate;
                rawRanges.Add((s, e));
            }
        }

        foreach (var alloc in asset.Allocations)
        {
            var display = AssetAvailabilityPolicy.GetDisplayOccupiedInterval(alloc, monthStart, monthEnd, nowUtc);
            if (display.HasValue)
            {
                rawRanges.Add((display.Value.StartDate, display.Value.EndDate));
            }

            if (alloc.Status == AllocationStatus.Held &&
                alloc.HoldExpiresAt.HasValue &&
                alloc.HoldExpiresAt.Value > nowUtc)
            {
                if (nearestHoldExpiry is null || alloc.HoldExpiresAt.Value < nearestHoldExpiry.Value)
                {
                    nearestHoldExpiry = alloc.HoldExpiresAt.Value;
                }
            }
            else if (alloc.Status == AllocationStatus.Allocated &&
                     alloc.HandedOverAt.HasValue &&
                     !alloc.ReturnedAt.HasValue &&
                     alloc.RentalPeriod.EndDate < DateOnly.MaxValue)
            {
                var overdueTransitionUtc = BusinessDateTime.StartOfDayUtc(alloc.RentalPeriod.EndDate.AddDays(1));
                if (overdueTransitionUtc > nowUtc)
                {
                    if (nearestHoldExpiry is null || overdueTransitionUtc < nearestHoldExpiry.Value)
                    {
                        nearestHoldExpiry = overdueTransitionUtc;
                    }
                }
            }
        }

        var merged = MergeRanges(rawRanges);
        return (merged, nearestHoldExpiry);
    }

    private static IReadOnlyList<BusyRangeDto> CalculateRangeOccupants(
        InventoryAsset? asset,
        DateOnly windowStart,
        DateOnly windowEnd,
        DateTimeOffset nowUtc)
    {
        if (asset is null)
        {
            return Array.Empty<BusyRangeDto>();
        }

        var rawRanges = new List<(DateOnly Start, DateOnly End)>();

        foreach (var block in asset.CalendarBlocks)
        {
            if (block.StartDate <= windowEnd && block.EndDate >= windowStart)
            {
                var s = block.StartDate < windowStart ? windowStart : block.StartDate;
                var e = block.EndDate > windowEnd ? windowEnd : block.EndDate;
                rawRanges.Add((s, e));
            }
        }

        foreach (var alloc in asset.Allocations)
        {
            var display = AssetAvailabilityPolicy.GetDisplayOccupiedInterval(alloc, windowStart, windowEnd, nowUtc);
            if (display.HasValue)
            {
                rawRanges.Add((display.Value.StartDate, display.Value.EndDate));
            }
        }

        return MergeRanges(rawRanges);
    }

    public static IReadOnlyList<BusyRangeDto> MergeRanges(IEnumerable<(DateOnly Start, DateOnly End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
        if (sorted.Count == 0)
        {
            return Array.Empty<BusyRangeDto>();
        }

        var merged = new List<(DateOnly Start, DateOnly End)>();
        var current = sorted[0];

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            // An toàn trước tràn số tại 9999-12-31 bằng cách dùng DayNumber
            if (next.Start.DayNumber <= current.End.DayNumber + 1)
            {
                if (next.End > current.End)
                {
                    current = (current.Start, next.End);
                }
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);

        return merged.Select(m => new BusyRangeDto(m.Start, m.End)).ToList();
    }
}
