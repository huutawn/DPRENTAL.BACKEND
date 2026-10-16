using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dprental.MediaService.Application.Services;

public partial class ImageVariantBackfillService : IImageVariantBackfillService
{
    private const int MaxTransientRetries = 3;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImageVariantBackfillService> _logger;

    public ImageVariantBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<ImageVariantBackfillService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BackfillSummary> RunBackfillAsync(
        BackfillOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        int batchSize = options.BatchSize is > 0 and <= 500 ? options.BatchSize : 25;
        LogBackfillStart(_logger, options.DryRun, batchSize, options.AfterId, options.MediaId);

        int totalProcessed = 0;
        int succeededCount = 0;
        int skippedCount = 0;
        int failedCount = 0;
        var failedIds = new List<Guid>();
        Guid? lastCheckpoint = options.AfterId;

        if (options.MediaId.HasValue)
        {
            var outcome = await ProcessSingleAssetAsync(options.MediaId.Value, options.DryRun, cancellationToken);
            totalProcessed++;
            if (outcome == ProcessOutcome.Succeeded) succeededCount++;
            else if (outcome == ProcessOutcome.Skipped) skippedCount++;
            else
            {
                failedCount++;
                failedIds.Add(options.MediaId.Value);
            }

            return new BackfillSummary(totalProcessed, succeededCount, skippedCount, failedCount, failedIds, options.MediaId.Value);
        }

        Guid? currentAfterId = options.AfterId;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<Guid> candidateIds;
            using (var batchScope = _scopeFactory.CreateScope())
            {
                var scopedAssetRepo = batchScope.ServiceProvider.GetRequiredService<IMediaAssetRepository>();
                var batch = await scopedAssetRepo.GetReadyImagesMissingVariantsAsync(
                    batchSize,
                    currentAfterId,
                    ImageVariantPolicy.CurrentProcessingVersion,
                    cancellationToken);

                if (batch.Count == 0)
                {
                    break;
                }

                candidateIds = batch.Select(a => a.Id).ToList();
            }

            foreach (var assetId in candidateIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                totalProcessed++;
                lastCheckpoint = assetId;
                currentAfterId = assetId;

                var outcome = await ProcessSingleAssetAsync(assetId, options.DryRun, cancellationToken);
                switch (outcome)
                {
                    case ProcessOutcome.Succeeded:
                        succeededCount++;
                        break;
                    case ProcessOutcome.Skipped:
                        skippedCount++;
                        break;
                    case ProcessOutcome.Failed:
                        failedCount++;
                        failedIds.Add(assetId);
                        break;
                }
            }
        }

        LogBackfillComplete(_logger, totalProcessed, succeededCount, skippedCount, failedCount, lastCheckpoint);
        return new BackfillSummary(totalProcessed, succeededCount, skippedCount, failedCount, failedIds, lastCheckpoint);
    }

    private async Task<ProcessOutcome> ProcessSingleAssetAsync(
        Guid assetId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        using var checkScope = _scopeFactory.CreateScope();
        var checkRepo = checkScope.ServiceProvider.GetRequiredService<IMediaAssetRepository>();
        var asset = await checkRepo.GetByIdUntrackedAsync(assetId, cancellationToken);
        if (asset == null)
        {
            LogAssetNotFound(_logger, assetId);
            return ProcessOutcome.Failed;
        }

        if (asset.Status != MediaStatus.Ready)
        {
            return ProcessOutcome.Skipped;
        }

        string effectiveContentType = asset.ActualContentType ?? asset.DeclaredContentType;
        if (!ImageVariantPolicy.IsProcessableImage(effectiveContentType))
        {
            return ProcessOutcome.Skipped;
        }

        int existingCount = (asset.ImageVariants ?? [])
            .Count(v => v.ProcessingVersion == ImageVariantPolicy.CurrentProcessingVersion);

        if (existingCount >= 3)
        {
            return ProcessOutcome.Skipped;
        }

        if (string.IsNullOrWhiteSpace(asset.StorageKey))
        {
            LogMissingStorageKey(_logger, asset.Id);
            return ProcessOutcome.Failed;
        }

        if (dryRun)
        {
            LogDryRunAsset(_logger, asset.Id, asset.OriginalFileName, effectiveContentType);
            return ProcessOutcome.Succeeded;
        }

        int attempt = 0;
        while (attempt < MaxTransientRetries && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            using var runScope = _scopeFactory.CreateScope();
            var runAssetRepo = runScope.ServiceProvider.GetRequiredService<IMediaAssetRepository>();
            var runStorage = runScope.ServiceProvider.GetRequiredService<IObjectStorageService>();
            var runVariantService = runScope.ServiceProvider.GetRequiredService<IImageVariantService>();

            var scopedAsset = await runAssetRepo.GetByIdAsync(assetId, cancellationToken);
            if (scopedAsset == null || scopedAsset.Status != MediaStatus.Ready)
            {
                return ProcessOutcome.Skipped;
            }

            Stream? stream = null;
            try
            {
                stream = await runStorage.GetObjectStreamAsync(scopedAsset.StorageKey!, cancellationToken);
                if (stream == null || stream.Length == 0)
                {
                    LogSourceStreamMissing(_logger, scopedAsset.Id, scopedAsset.StorageKey!);
                    return ProcessOutcome.Failed;
                }

                var result = await runVariantService.ProcessAndStoreVariantsAsync(
                    scopedAsset,
                    stream,
                    session: null,
                    cancellationToken);

                if (result.IsSuccess)
                {
                    return ProcessOutcome.Succeeded;
                }

                // Lỗi ảnh hỏng / vượt pixel không phải transient, dừng retry cho file này
                if (result.Error.Code is "CorruptImage" or "PixelLimitExceeded" or "FileSizeExceeded")
                {
                    LogCorruptedAsset(_logger, scopedAsset.Id, result.Error.Code, result.Error.Message);
                    return ProcessOutcome.Failed;
                }

                LogRetryTransient(_logger, scopedAsset.Id, attempt, result.Error.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogRetryTransient(_logger, scopedAsset.Id, attempt, ex.Message);
            }
            finally
            {
                if (stream != null)
                {
                    await stream.DisposeAsync();
                }
            }

            if (attempt < MaxTransientRetries && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200 * attempt, cancellationToken);
            }
        }

        LogExhaustedRetries(_logger, assetId);
        return ProcessOutcome.Failed;
    }

    private enum ProcessOutcome
    {
        Succeeded,
        Skipped,
        Failed
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Starting image variant backfill. DryRun={DryRun}, BatchSize={BatchSize}, AfterId={AfterId}, MediaId={MediaId}")]
    private static partial void LogBackfillStart(ILogger logger, bool dryRun, int batchSize, Guid? afterId, Guid? mediaId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "MediaAsset {MediaId} was not found for backfill.")]
    private static partial void LogAssetNotFound(ILogger logger, Guid mediaId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "[DRY-RUN] Would process asset {MediaId} ({FileName}, {ContentType})")]
    private static partial void LogDryRunAsset(ILogger logger, Guid mediaId, string fileName, string contentType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Asset {MediaId} has no storage key.")]
    private static partial void LogMissingStorageKey(ILogger logger, Guid mediaId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message = "Source stream is missing or empty for asset {MediaId} (StorageKey: {StorageKey})")]
    private static partial void LogSourceStreamMissing(ILogger logger, Guid mediaId, string storageKey);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message = "Failed to read source object for asset {MediaId}: {Error}")]
    private static partial void LogStorageReadError(ILogger logger, Guid mediaId, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Transient failure processing asset {MediaId} (Attempt {Attempt}): {Error}")]
    private static partial void LogRetryTransient(ILogger logger, Guid mediaId, int attempt, string error);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error,
        Message = "Asset {MediaId} is corrupt or unprocessable ({Code}): {Message}")]
    private static partial void LogCorruptedAsset(ILogger logger, Guid mediaId, string code, string message);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error,
        Message = "Exhausted all retries for asset {MediaId}. Backfill failed for this file.")]
    private static partial void LogExhaustedRetries(ILogger logger, Guid mediaId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information,
        Message = "Image variant backfill finished. Total: {Total}, Succeeded: {Succeeded}, Skipped: {Skipped}, Failed: {Failed}, Checkpoint: {Checkpoint}")]
    private static partial void LogBackfillComplete(ILogger logger, int total, int succeeded, int skipped, int failed, Guid? checkpoint);
}
