using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Dprental.MediaService.Domain.Processing;
using Microsoft.Extensions.Logging;

namespace Dprental.MediaService.Application.Services;

public partial class ImageVariantService : IImageVariantService
{
    private readonly IImageProcessor _imageProcessor;
    private readonly IObjectStorageService _objectStorage;
    private readonly IMediaAssetRepository _assetRepository;
    private readonly IMediaUploadSessionRepository _sessionRepository;
    private readonly IMediaCleanupRepository _cleanupRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ImageVariantService> _logger;

    public ImageVariantService(
        IImageProcessor imageProcessor,
        IObjectStorageService objectStorage,
        IMediaAssetRepository assetRepository,
        IMediaUploadSessionRepository sessionRepository,
        IMediaCleanupRepository cleanupRepository,
        IUnitOfWork unitOfWork,
        ILogger<ImageVariantService> logger)
    {
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _objectStorage = objectStorage ?? throw new ArgumentNullException(nameof(objectStorage));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _cleanupRepository = cleanupRepository ?? throw new ArgumentNullException(nameof(cleanupRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<IReadOnlyList<MediaImageVariantDto>>> ProcessAndStoreVariantsAsync(
        MediaAsset asset,
        Stream sourceStream,
        MediaUploadSession? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(sourceStream);

        string effectiveContentType = asset.ActualContentType ?? asset.DeclaredContentType;
        if (!ImageVariantPolicy.IsProcessableImage(effectiveContentType))
        {
            return Result.Success<IReadOnlyList<MediaImageVariantDto>>([]);
        }

        // Kiểm tra tính lũy kế (idempotency/replay) nếu đã có đủ 3 bản của phiên bản hiện tại
        var publishedVariants = (asset.ImageVariants ?? [])
            .Where(v => v.ProcessingVersion == ImageVariantPolicy.CurrentProcessingVersion)
            .OrderBy(v => v.Width)
            .ToList();

        if (publishedVariants.Count >= 3)
        {
            return Result.Success<IReadOnlyList<MediaImageVariantDto>>(
                publishedVariants.Select(v => ToDto(asset.Id, v)).ToList());
        }

        Guid runId = Guid.NewGuid();
        var specs = ImageVariantPolicy.RequiredVariants;
        var keyMap = specs.ToDictionary(
            s => s.Name,
            s => $"media/{asset.Id:N}/variants/v{ImageVariantPolicy.CurrentProcessingVersion}_{s.Name}_{runId:N}.webp");

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Lưu dấu lần xử lý trước khi gọi MinIO để dọn dẹp được ngay cả khi process dừng giữa chừng
        foreach (string storageKey in keyMap.Values)
        {
            await _cleanupRepository.AddAsync(new MediaCleanupItem
            {
                Id = Guid.NewGuid(),
                StorageKey = storageKey,
                Reason = "PendingVariantProcessing",
                Status = CleanupItemStatus.Pending,
                CreatedAtUtc = now,
                EligibleAtUtc = now.AddMinutes(15)
            }, cancellationToken);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            LogTrackingFailure(_logger, asset.Id, ex.Message);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Failure("DatabaseError", "Không thể khởi tạo theo dõi dọn dẹp biến thể ảnh."));
        }

        // Xử lý biến đổi ảnh qua SkiaSharp
        IReadOnlyList<ProcessedImageVariant> processed;
        try
        {
            processed = await _imageProcessor.GenerateVariantsAsync(
                sourceStream,
                effectiveContentType,
                specs,
                cancellationToken);
        }
        catch (ImageDecodeException ex)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Unprocessable("CorruptImage", ex.Message));
        }
        catch (ImagePixelLimitExceededException ex)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Unprocessable("PixelLimitExceeded", ex.Message));
        }
        catch (ImageEncodeException ex)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Failure("EncodeError", ex.Message));
        }
        catch (Exception ex)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Failure("ImageProcessingFailed", ex.Message));
        }

        // Lưu đủ 3 object WebP lên MinIO
        var uploadedKeys = new List<string>(processed.Count);
        try
        {
            foreach (var variant in processed)
            {
                string key = keyMap[variant.Name];
                using var ms = new MemoryStream(variant.Data);
                await _objectStorage.PutObjectAsync(
                    key,
                    ms,
                    variant.SizeBytes,
                    variant.ContentType,
                    cancellationToken);
                uploadedKeys.Add(key);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            throw;
        }
        catch (Exception ex)
        {
            LogUploadFailure(_logger, asset.Id, ex.Message);
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Failure("StorageError", "Không thể lưu trữ tệp WebP lên storage."));
        }

        // Chỉ công bố bộ variant sau khi lưu đủ 3 object. Không giữ transaction DB trong lúc gọi MinIO.
        var variantEntities = processed.Select(v => new MediaImageVariant
        {
            Id = Guid.NewGuid(),
            MediaAssetId = asset.Id,
            VariantName = v.Name,
            ProcessingVersion = ImageVariantPolicy.CurrentProcessingVersion,
            StorageKey = keyMap[v.Name],
            ContentType = v.ContentType,
            Width = v.Width,
            Height = v.Height,
            SizeBytes = v.SizeBytes,
            ChecksumSha256 = v.ChecksumSha256,
            CreatedAtUtc = now
        }).ToList();

        bool isClaimedByCleanup = await _cleanupRepository.HasClaimedMarkersAsync(keyMap.Values, cancellationToken);
        if (isClaimedByCleanup)
        {
            await CleanupOrphanedKeysAsync(keyMap.Values);
            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Conflict("CleanupPreempted", "Tác vụ dọn dẹp đã giữ lease trên object, hủy bỏ công bố biến thể."));
        }

        try
        {
            // Xóa dấu cleanup tạm vì các object này đã thành công và được công bố
            await _cleanupRepository.RemoveRangeByStorageKeysAsync(keyMap.Values, cancellationToken);
            await _assetRepository.AddVariantsAsync(variantEntities, cancellationToken);

            if (session != null)
            {
                asset.Status = MediaStatus.Ready;
                asset.UpdatedAtUtc = now;

                session.Status = UploadSessionStatus.Completed;
                session.CompletedAtUtc = now;

                await _assetRepository.UpdateAsync(asset, cancellationToken);
                await _sessionRepository.UpdateAsync(session, cancellationToken);

                // Lên lịch dọn dẹp tệp tạm upload ban đầu
                await _cleanupRepository.AddAsync(new MediaCleanupItem
                {
                    Id = Guid.NewGuid(),
                    StorageKey = session.TemporaryStorageKey,
                    Reason = "UploadCompleted",
                    Status = CleanupItemStatus.Pending,
                    CreatedAtUtc = now,
                    EligibleAtUtc = session.ExpiresAtUtc.AddMinutes(1)
                }, cancellationToken);
            }
            else
            {
                // Trường hợp backfill
                asset.UpdatedAtUtc = now;
                await _assetRepository.UpdateAsync(asset, cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            var dtoList = variantEntities
                .OrderBy(v => v.Width)
                .Select(v => ToDto(asset.Id, v))
                .ToList();

            return Result.Success<IReadOnlyList<MediaImageVariantDto>>(dtoList);
        }
        catch (MediaWriteConflictException)
        {
            // Thua tranh chấp: xóa object vừa tạo và trả về bộ đã được bên thắng công bố
            await CleanupOrphanedKeysAsync(keyMap.Values);

            var reloaded = await _assetRepository.GetByIdUntrackedAsync(asset.Id, cancellationToken);
            var reloadedVariants = (reloaded?.ImageVariants ?? [])
                .Where(v => v.ProcessingVersion == ImageVariantPolicy.CurrentProcessingVersion)
                .OrderBy(v => v.Width)
                .ToList();

            if (reloadedVariants.Count >= 3)
            {
                return Result.Success<IReadOnlyList<MediaImageVariantDto>>(
                    reloadedVariants.Select(v => ToDto(asset.Id, v)).ToList());
            }

            return Result.Failure<IReadOnlyList<MediaImageVariantDto>>(
                Error.Conflict("ConcurrencyConflict", "Đang có tác vụ khác cập nhật biến thể của media."));
        }
    }

    private async Task CleanupOrphanedKeysAsync(IEnumerable<string> storageKeys)
    {
        foreach (string key in storageKeys)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _objectStorage.DeleteObjectAsync(key, cts.Token);
            }
            catch (Exception ex)
            {
                LogCleanupFailure(_logger, key, ex.Message);
            }
        }
    }

    private static MediaImageVariantDto ToDto(Guid mediaId, MediaImageVariant entity) =>
        new(
            entity.VariantName,
            entity.Width,
            entity.Height,
            entity.ContentType,
            entity.SizeBytes,
            ImageVariantPolicy.BuildContentPath(mediaId, entity.VariantName));

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Could not track variant cleanup for media {MediaId}: {Error}")]
    private static partial void LogTrackingFailure(ILogger logger, Guid mediaId, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to upload variant for media {MediaId}: {Error}")]
    private static partial void LogUploadFailure(ILogger logger, Guid mediaId, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Best effort delete failed for storage key {Key}: {Error}")]
    private static partial void LogCleanupFailure(ILogger logger, string key, string error);
}
