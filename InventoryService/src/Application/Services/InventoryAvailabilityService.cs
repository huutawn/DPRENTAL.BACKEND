using Dprental.InventoryService.Domain.Caching;
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
    private readonly IInventoryAvailabilityCache _cache;
    private readonly InventoryCacheOptions _cacheOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InventoryAvailabilityService>? _logger;

    public InventoryAvailabilityService(
        IInventoryAssetRepository assetRepository,
        IInventoryAvailabilityCache cache,
        TimeProvider timeProvider)
        : this(assetRepository, cache, null, timeProvider, null)
    {
    }

    public InventoryAvailabilityService(
        IInventoryAssetRepository assetRepository,
        IInventoryAvailabilityCache cache,
        IOptions<InventoryCacheOptions>? cacheOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<InventoryAvailabilityService>? logger = null)
    {
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
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

        // Batch luôn đọc trực tiếp PostgreSQL
        var snapshots = await _assetRepository.GetAvailabilitySnapshotsAsync(
            deduplicatedIds,
            validStart,
            validEnd,
            cancellationToken);

        // Lấy clock sau khi đọc DB xong để đánh giá occupancy chính xác
        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

        var assetsById = snapshots.ToDictionary(a => a.Id);

        var unavailableAssetIds = new List<Guid>();
        var notFoundAssetIds = new List<Guid>();

        foreach (var id in deduplicatedIds)
        {
            if (!assetsById.TryGetValue(id, out var asset))
            {
                notFoundAssetIds.Add(id);
                continue;
            }

            var checkResult = AssetAvailabilityPolicy.CheckAvailability(
                asset,
                periodResult.Value,
                nowUtc,
                asset.Allocations,
                asset.CalendarBlocks);

            if (!checkResult.IsSuccess)
            {
                unavailableAssetIds.Add(id);
            }
        }

        return Result.Success(new CheckAvailabilityBatchResponse(
            unavailableAssetIds,
            notFoundAssetIds));
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
        var normalTtl = TimeSpan.FromSeconds(_cacheOptions.TtlSeconds > 0 ? _cacheOptions.TtlSeconds : 60);

        // Xác định danh sách các tháng nằm trong khoảng [validFrom, validTo]
        var months = BuildMonthList(validFrom, validTo);

        IReadOnlyDictionary<string, CachedMonthRanges?>? cachedMonths = null;
        if (_cache.IsEnabled)
        {
            try
            {
                var monthKeyList = months.Select(m => m.Key).ToList();
                var preReadNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
                cachedMonths = await _cache.GetMonthRangesBulkAsync(assetId, monthKeyList, preReadNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi đọc cache busy ranges cho tài sản {AssetId}. Fallback đọc PostgreSQL.", assetId);
                cachedMonths = null;
            }
        }

        // Kiểm tra hit bằng clock mới sau cache read
        var postCacheReadNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var monthRanges = new Dictionary<string, (IReadOnlyList<BusyRangeDto> Ranges, DateTimeOffset ExpiresAtUtc)>();
        var missingMonths = new List<(string Key, DateOnly MonthStart, DateOnly MonthEnd)>();

        foreach (var m in months)
        {
            if (cachedMonths != null &&
                cachedMonths.TryGetValue(m.Key, out var cached) &&
                cached != null &&
                cached.ExpiresAtUtc > postCacheReadNow)
            {
                monthRanges[m.Key] = (cached.Ranges, cached.ExpiresAtUtc);
            }
            else
            {
                missingMonths.Add(m);
            }
        }

        if (missingMonths.Count > 0)
        {
            var minMissingDate = missingMonths.Min(m => m.MonthStart);
            var maxMissingDate = missingMonths.Max(m => m.MonthEnd);

            // Neo deadline normal TTL vào thời điểm bắt đầu đọc snapshot
            var snapshotStartedAt = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

            var snapshots = await _assetRepository.GetAvailabilitySnapshotsAsync(
                new[] { assetId },
                minMissingDate,
                maxMissingDate,
                cancellationToken);

            // Clock mới sau khi snapshot I/O hoàn tất để tính occupancy chính xác
            var postSnapshotNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
            var snapshotAsset = snapshots.FirstOrDefault(a => a.Id == assetId);

            foreach (var m in missingMonths)
            {
                var (rangesForMonth, nearestHoldExpiry) = CalculateMonthOccupants(snapshotAsset, m.MonthStart, m.MonthEnd, postSnapshotNow);

                // Tính toán deadline: min(normal TTL deadline neo từ snapshot bắt đầu, nearest active Hold expiry)
                DateTimeOffset deadline;
                var normalDeadline = snapshotStartedAt.Add(normalTtl);
                if (nearestHoldExpiry.HasValue)
                {
                    deadline = nearestHoldExpiry.Value < normalDeadline ? nearestHoldExpiry.Value : normalDeadline;
                }
                else
                {
                    deadline = normalDeadline;
                }

                monthRanges[m.Key] = (rangesForMonth, deadline);

                // Tính lại remaining TTL trước khi ghi; bỏ ghi nếu deadline đã qua
                var preWriteNow = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
                if (deadline > preWriteNow && _cache.IsEnabled)
                {
                    try
                    {
                        await _cache.SetMonthRangesAsync(
                            assetId,
                            m.Key,
                            rangesForMonth,
                            deadline,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Lỗi ghi cache cho tài sản {AssetId}, tháng {MonthKey}.", assetId, m.Key);
                    }
                }
            }
        }

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
            if (!AssetAvailabilityPolicy.OccupiesCalendar(alloc, nowUtc))
            {
                continue;
            }

            if (alloc.RentalPeriod.StartDate <= monthEnd && alloc.RentalPeriod.EndDate >= monthStart)
            {
                var s = alloc.RentalPeriod.StartDate < monthStart ? monthStart : alloc.RentalPeriod.StartDate;
                var e = alloc.RentalPeriod.EndDate > monthEnd ? monthEnd : alloc.RentalPeriod.EndDate;
                rawRanges.Add((s, e));

                if (alloc.Status == AllocationStatus.Held &&
                    alloc.HoldExpiresAt.HasValue &&
                    alloc.HoldExpiresAt.Value > nowUtc)
                {
                    if (nearestHoldExpiry is null || alloc.HoldExpiresAt.Value < nearestHoldExpiry.Value)
                    {
                        nearestHoldExpiry = alloc.HoldExpiresAt.Value;
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
            if (!AssetAvailabilityPolicy.OccupiesCalendar(alloc, nowUtc))
            {
                continue;
            }

            if (alloc.RentalPeriod.StartDate <= windowEnd && alloc.RentalPeriod.EndDate >= windowStart)
            {
                var s = alloc.RentalPeriod.StartDate < windowStart ? windowStart : alloc.RentalPeriod.StartDate;
                var e = alloc.RentalPeriod.EndDate > windowEnd ? windowEnd : alloc.RentalPeriod.EndDate;
                rawRanges.Add((s, e));
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
