using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Recommendation;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Options;
using Dprental.InventoryService.Application.Search;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class PublicEquipmentListingService
{
    /// <inheritdoc />
    public async Task<Result<PublicEquipmentListingResponse>> GetPublicListingByIdAsync(
        Guid id,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (id == Guid.Empty)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        var today = BusinessDateTime.ToVietnamDate(nowUtc);

        string scope = $"listing:{id:D}";
        string queryType = "public-detail";
        string inputHash = today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        var result = await _cache.GetOrCreateAsync<PublicEquipmentListingResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var snapshot = await _listingRepository.GetCalendarSnapshotAsync(id, today, today, ct);
                if (snapshot is null || snapshot.Status != EquipmentListingStatus.Active)
                {
                    return Error.NotFound("LISTING_NOT_FOUND", "Listing không tồn tại.");
                }

                var evaluationNowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());

                int totalQuantity = snapshot.Assets.Count(a => a.AcceptingBookings);
                int availableQuantity = snapshot.Assets.Count(a =>
                    a.AcceptingBookings &&
                    a.CalendarBlocks.Count == 0 &&
                    !a.Allocations.Any(al =>
                        al.Status == AllocationStatus.Allocated ||
                        (al.Status == AllocationStatus.Held && al.HoldExpiresAt > evaluationNowUtc)));

                var response = PublicEquipmentListingMapper.ToResponse(
                    snapshot,
                    totalQuantity,
                    availableQuantity,
                    _mediaOptions.PublicGatewayUrl);

                if (_catalogClient is not null)
                {
                    var categoryResult = await _catalogClient.GetPublishedEquipmentTypeAsync(snapshot.EquipmentTypeId, ct);
                    if (categoryResult.IsSuccess)
                    {
                        response = response with { CategoryIds = categoryResult.Value.CategoryIds };
                    }
                }

                var baseTtl = TimeSpan.FromSeconds(_cacheOptions.DynamicTtlSeconds > 0 ? _cacheOptions.DynamicTtlSeconds : 15);
                DateTimeOffset? nearestHold = snapshot.Assets
                    .SelectMany(a => a.Allocations)
                    .Where(al => al.Status == AllocationStatus.Held && al.HoldExpiresAt.HasValue && al.HoldExpiresAt.Value > evaluationNowUtc)
                    .Select(al => al.HoldExpiresAt)
                    .OrderBy(t => t!.Value)
                    .FirstOrDefault();

                var allAllocations = snapshot.Assets.SelectMany(a => a.Allocations);
                var deadline = Dprental.InventoryService.Application.Caching.CacheKeyHelper.CalculateOccupancyDeadline(
                    evaluationNowUtc.Add(baseTtl),
                    nearestHold,
                    allAllocations,
                    evaluationNowUtc);

                DateTimeOffset? finalDeadline = deadline > evaluationNowUtc ? deadline : null;
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<PublicEquipmentListingResponse>(response, finalDeadline));
            },
            cancellationToken);

        if (result.IsSuccess && _behaviorRecorder != null)
        {
            await _behaviorRecorder.RecordListingViewedAsync(
                id,
                result.Value.EquipmentTypeId,
                callerContext,
                cancellationToken);
        }

        return result;
    }
    /// <inheritdoc />
    public async Task<Result<EquipmentListingStatisticsResponse>> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        string scope = "public-discovery";
        string queryType = "public-stats";
        string inputHash = "active";

        return await _cache.GetOrCreateAsync<EquipmentListingStatisticsResponse>(
            scope,
            queryType,
            inputHash,
            async (genToken, ct) =>
            {
                var criteria = new EquipmentListingStatisticsCriteria(
                    OwnerUserId: null,
                    Status: EquipmentListingStatus.Active);

                var result = await _listingRepository.GetListingStatisticsAsync(criteria, ct);
                var statsResponse = new EquipmentListingStatisticsResponse(result.ByEquipmentType, result.ByBrand);
                var ttl = _timeProvider.GetUtcNow().AddSeconds(_cacheOptions.MetadataTtlSeconds > 0 ? _cacheOptions.MetadataTtlSeconds : 120);
                return Result.Success(new Dprental.InventoryService.Application.Caching.CachedRead<EquipmentListingStatisticsResponse>(statsResponse, ttl));
            },
            cancellationToken);
    }
}
