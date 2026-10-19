using System.Globalization;
using System.Text.Json;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RecommendationService.Application.Services;

public sealed class SnapshotPublishingService : ISnapshotPublishingService
{
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(300);
    private const int UserPageSize = 100;
    private const int MaxUserInterests = 20;
    private const int MaxGlobalPopularListings = 100;

    private readonly IUserEquipmentTypeInterestRepository _userInterestRepository;
    private readonly IListingPopularityRepository _listingPopularityRepository;
    private readonly IValkeyProjectionRepository _valkeyRepository;
    private readonly ILogger<SnapshotPublishingService> _logger;

    public SnapshotPublishingService(
        IUserEquipmentTypeInterestRepository userInterestRepository,
        IListingPopularityRepository listingPopularityRepository,
        IValkeyProjectionRepository valkeyRepository,
        ILogger<SnapshotPublishingService> logger)
    {
        _userInterestRepository = userInterestRepository;
        _listingPopularityRepository = listingPopularityRepository;
        _valkeyRepository = valkeyRepository;
        _logger = logger;
    }

    public async Task PublishSnapshotsAsync(CancellationToken cancellationToken)
    {
        string generatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);

        try
        {
            // 1. Publish Global Popular Listings Snapshot
            await PublishGlobalPopularListingsSnapshotAsync(generatedAt, cancellationToken);

            // 2. Publish User Equipment Types Snapshot (paged)
            await PublishUserSnapshotsAsync(generatedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error occurred during snapshot publication cycle. Non-fatal for ingestion.");
        }
    }

    private async Task PublishGlobalPopularListingsSnapshotAsync(string generatedAt, CancellationToken cancellationToken)
    {
        var topListings = await _listingPopularityRepository.GetTopPopularListingsAsync(MaxGlobalPopularListings, cancellationToken);
        var items = topListings
            .Select(x => new GlobalPopularListingsSnapshotItem(x.EquipmentListingId.ToString("D"), x.Score))
            .ToList();

        var snapshot = new GlobalPopularListingsSnapshot(
            Version: 1,
            GeneratedAt: generatedAt,
            Items: items);

        string json = JsonSerializer.Serialize(snapshot);
        bool success = await _valkeyRepository.SetGlobalPopularListingsSnapshotAsync(json, generatedAt, SnapshotTtl, cancellationToken);
        if (success)
        {
            _logger.LogDebug("Published global popular listings snapshot ({Count} items).", items.Count);
        }
    }

    private async Task PublishUserSnapshotsAsync(string generatedAt, CancellationToken cancellationToken)
    {
        int skip = 0;
        int totalPublished = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var userIds = await _userInterestRepository.GetDistinctUserIdsPagedAsync(skip, UserPageSize, cancellationToken);
            if (userIds.Count == 0)
            {
                break;
            }

            foreach (var userId in userIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var topInterests = await _userInterestRepository.GetTopInterestsAsync(userId, MaxUserInterests, cancellationToken);
                var items = topInterests
                    .Select(x => new UserEquipmentTypesSnapshotItem(x.EquipmentTypeId.ToString("D"), x.Score))
                    .ToList();

                var snapshot = new UserEquipmentTypesSnapshot(
                    Version: 1,
                    GeneratedAt: generatedAt,
                    Items: items);

                string json = JsonSerializer.Serialize(snapshot);
                bool success = await _valkeyRepository.SetUserEquipmentTypesSnapshotAsync(userId, json, generatedAt, SnapshotTtl, cancellationToken);
                if (success)
                {
                    totalPublished++;
                }
            }

            skip += userIds.Count;
        }

        _logger.LogDebug("Published user interest snapshots for {Count} users.", totalPublished);
    }
}
