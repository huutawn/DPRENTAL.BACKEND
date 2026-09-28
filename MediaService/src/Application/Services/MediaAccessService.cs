using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.MediaService.Application.Services;

public sealed class MediaAccessService : IMediaAccessService
{
    private static readonly Action<ILogger, Guid, string, Exception?> LogReadUrlFailure =
        LoggerMessage.Define<Guid, string>(LogLevel.Warning, new EventId(1, "ReadUrlFailure"),
            "Failed to create read URL for media {MediaId}: {ErrorType}");

    public const string VerifyScope = "media.verify";
    public const string ReadScope = "media.read";
    public const string PublishScope = "media.publish";

    private readonly IMediaAssetRepository _assets;
    private readonly IObjectStorageService _storage;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MediaAccessService> _logger;
    private readonly TimeSpan _readUrlLifetime;

    public MediaAccessService(
        IMediaAssetRepository assets,
        IObjectStorageService storage,
        IUnitOfWork unitOfWork,
        IOptions<MediaAccessSettings> settings,
        ILogger<MediaAccessService> logger)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        int seconds = settings?.Value.ReadUrlExpirySeconds ?? throw new ArgumentNullException(nameof(settings));
        if (seconds is < 1 or > 900)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Read URL expiry must be between 1 and 900 seconds.");
        }

        _readUrlLifetime = TimeSpan.FromSeconds(seconds);
    }

    public async Task<Result<MediaVerificationDto>> VerifyMediaAsync(
        ServiceCaller caller, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var found = await FindServiceAssetAsync(caller, VerifyScope, mediaId, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<MediaVerificationDto>(found.Error);
        }

        MediaAsset asset = found.Value;
        if (asset.Status != MediaStatus.Ready)
        {
            return Result.Failure<MediaVerificationDto>(
                Error.Conflict("MediaNotReady", "Media chưa sẵn sàng để sử dụng."));
        }

        return Result.Success(new MediaVerificationDto(
            asset.Id, asset.Status.ToString(),
            asset.CreatedByUserId, asset.OriginalFileName,
            asset.ActualContentType ?? asset.DeclaredContentType,
            asset.ActualSizeBytes ?? asset.DeclaredSizeBytes, asset.CreatedAtUtc));
    }

    public async Task<Result<MediaMetadataDto>> GetMetadataAsync(
        ServiceCaller caller, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var found = await FindServiceAssetAsync(caller, ReadScope, mediaId, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<MediaMetadataDto>(found.Error);
        }

        MediaAsset asset = found.Value;
        if (asset.Status == MediaStatus.Deleted)
        {
            return Result.Failure<MediaMetadataDto>(MediaNotFound());
        }

        return Result.Success(new MediaMetadataDto(
            asset.Id, asset.OriginalFileName, asset.ActualContentType,
            asset.ActualSizeBytes, asset.Status.ToString(),
            asset.Visibility.ToString(), asset.CreatedAtUtc));
    }

    public async Task<Result<MediaReadUrlDto>> CreatePrivateReadUrlAsync(
        ServiceCaller caller, Guid mediaId, MediaAccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Action, "read", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<MediaReadUrlDto>(
                Error.Validation("InvalidAction", "MVP chỉ hỗ trợ thao tác read."));
        }

        var found = await FindServiceAssetAsync(caller, ReadScope, mediaId, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<MediaReadUrlDto>(found.Error);
        }

        MediaAsset asset = found.Value;

        if (asset.Status != MediaStatus.Ready)
        {
            return Result.Failure<MediaReadUrlDto>(
                Error.Conflict("MediaNotReady", "Media chưa sẵn sàng để truy cập."));
        }

        return await CreateSignedReadUrlAsync(asset, request.Download, cancellationToken);
    }

    public async Task<Result<MediaReadUrlDto>> CreateUploaderPreviewUrlAsync(
        string authenticatedUserId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return Result.Failure<MediaReadUrlDto>(
                Error.Unauthorized("UnauthorizedUser", "Yêu cầu phải được xác thực từ một người dùng hợp lệ."));
        }

        if (mediaId == Guid.Empty)
        {
            return Result.Failure<MediaReadUrlDto>(InvalidMediaId());
        }

        MediaAsset? asset = await _assets.GetByIdAsync(mediaId, cancellationToken);
        if (asset is null || !string.Equals(asset.CreatedByUserId, authenticatedUserId.Trim(), StringComparison.Ordinal) ||
            asset.Status != MediaStatus.Ready)
        {
            return Result.Failure<MediaReadUrlDto>(MediaNotFound());
        }

        return await CreateSignedReadUrlAsync(asset, false, cancellationToken);
    }

    public async Task<Result<MediaReadUrlDto>> CreatePublicReadUrlAsync(
        Guid mediaId, bool download, CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty)
        {
            return Result.Failure<MediaReadUrlDto>(InvalidMediaId());
        }

        MediaAsset? asset = await _assets.GetByIdAsync(mediaId, cancellationToken);
        if (asset is null || asset.Status != MediaStatus.Ready ||
            asset.Visibility != MediaVisibility.Public)
        {
            return Result.Failure<MediaReadUrlDto>(MediaNotFound());
        }

        return await CreateSignedReadUrlAsync(asset, download, cancellationToken);
    }

    public async Task<Result<MediaVisibilityDto>> SetVisibilityAsync(
        ServiceCaller caller, Guid mediaId, ChangeMediaVisibilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MediaVisibility desired;
        if (string.Equals(request.Visibility, "public", StringComparison.OrdinalIgnoreCase))
        {
            desired = MediaVisibility.Public;
        }
        else if (string.Equals(request.Visibility, "private", StringComparison.OrdinalIgnoreCase))
        {
            desired = MediaVisibility.Private;
        }
        else
        {
            return Result.Failure<MediaVisibilityDto>(
                Error.Validation("InvalidVisibility", "Visibility phải là public hoặc private."));
        }

        var found = await FindServiceAssetAsync(caller, PublishScope, mediaId, cancellationToken);
        if (found.IsFailure)
        {
            return Result.Failure<MediaVisibilityDto>(found.Error);
        }

        MediaAsset asset = found.Value;

        if (asset.Status != MediaStatus.Ready)
        {
            return Result.Failure<MediaVisibilityDto>(
                Error.Conflict("MediaNotReady", "Media chưa sẵn sàng để thay đổi visibility."));
        }

        if (asset.Visibility == desired)
        {
            return Result.Success(new MediaVisibilityDto(mediaId, desired.ToString()));
        }

        asset.Visibility = desired;
        await _assets.UpdateAsync(asset, cancellationToken);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (MediaWriteConflictException)
        {
            MediaAsset? current = await _assets.GetByIdUntrackedAsync(mediaId, cancellationToken);
            if (current is not null && current.Status == MediaStatus.Ready &&
                current.Visibility == desired)
            {
                return Result.Success(new MediaVisibilityDto(mediaId, desired.ToString()));
            }

            return Result.Failure<MediaVisibilityDto>(
                Error.Conflict("VisibilityConflict", "Visibility đã thay đổi đồng thời. Vui lòng thử lại."));
        }

        return Result.Success(new MediaVisibilityDto(mediaId, desired.ToString()));
    }

    private async Task<Result<MediaAsset>> FindServiceAssetAsync(
        ServiceCaller caller, string requiredScope, Guid mediaId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Guid.TryParse(caller.Id, out Guid serviceId) || serviceId == Guid.Empty)
        {
            return Result.Failure<MediaAsset>(
                Error.Unauthorized("UnauthorizedService", "Yêu cầu phải được xác thực từ một service hợp lệ."));
        }

        if (!caller.HasScope(requiredScope))
        {
            return Result.Failure<MediaAsset>(
                Error.Forbidden("MissingServiceScope", "Service không có quyền thực hiện thao tác này."));
        }

        if (mediaId == Guid.Empty)
        {
            return Result.Failure<MediaAsset>(InvalidMediaId());
        }

        MediaAsset? asset = await _assets.GetByIdAsync(mediaId, cancellationToken);
        if (asset is null)
        {
            return Result.Failure<MediaAsset>(MediaNotFound());
        }

        return Result.Success(asset);
    }

    private async Task<Result<MediaReadUrlDto>> CreateSignedReadUrlAsync(
        MediaAsset asset, bool download, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.StorageKey))
        {
            return Result.Failure<MediaReadUrlDto>(
                Error.Failure("StorageKeyMissing", "Không thể truy cập tệp lúc này."));
        }

        try
        {
            ObjectMetadataResult? metadata = await _storage.GetObjectMetadataAsync(asset.StorageKey, cancellationToken);
            if (metadata is null)
            {
                return Result.Failure<MediaReadUrlDto>(MediaNotFound());
            }

            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(_readUrlLifetime);
            string url = await _storage.GeneratePresignedReadUrlAsync(
                asset.StorageKey, asset.ActualContentType ?? asset.DeclaredContentType,
                asset.OriginalFileName, download, _readUrlLifetime, cancellationToken);
            return Result.Success(new MediaReadUrlDto(asset.Id, url, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogReadUrlFailure(_logger, asset.Id, exception.GetType().Name, null);
            return Result.Failure<MediaReadUrlDto>(
                Error.Failure("StorageUnavailable", "Không thể truy cập tệp lúc này."));
        }
    }

    private static Error InvalidMediaId() =>
        Error.Validation("InvalidMediaId", "Mã media (mediaId) không hợp lệ.");

    private static Error MediaNotFound() =>
        Error.NotFound("MediaNotFound", "Không tìm thấy thông tin Media.");
}
