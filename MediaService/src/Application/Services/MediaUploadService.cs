using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public partial class MediaUploadService : IMediaUploadService
{
    private readonly IMediaUploadSessionRepository _sessionRepository;
    private readonly IMediaAssetRepository _assetRepository;
    private readonly IMediaCleanupRepository _cleanupRepository;
    private readonly IObjectStorageService _objectStorageService;
    private readonly IMediaAccessService _accessService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MediaUploadService> _logger;
    private readonly TimeSpan _sessionLifetime;

    public MediaUploadService(
        IMediaUploadSessionRepository sessionRepository,
        IMediaAssetRepository assetRepository,
        IMediaCleanupRepository cleanupRepository,
        IObjectStorageService objectStorageService,
        IMediaAccessService accessService,
        IUnitOfWork unitOfWork,
        IOptions<UploadSettings> settings,
        ILogger<MediaUploadService> logger)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _cleanupRepository = cleanupRepository ?? throw new ArgumentNullException(nameof(cleanupRepository));
        _objectStorageService = objectStorageService ?? throw new ArgumentNullException(nameof(objectStorageService));
        _accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionLifetime = TimeSpan.FromMinutes(settings?.Value.SessionLifetimeMinutes
            ?? throw new ArgumentNullException(nameof(settings)));
    }

    public async Task<Result<IReadOnlyList<UploadFileResultDto>>> UploadManyAsync(
        string authenticatedUserId,
        string? idempotencyKey,
        IReadOnlyList<UploadMediaRequest> files,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
            return Result.Failure<IReadOnlyList<UploadFileResultDto>>(
                Error.Unauthorized("UnauthorizedUser", "Yêu cầu phải được xác thực từ một người dùng hợp lệ."));
        if (files is null || files.Count is < 1 or > UploadMediaRequest.MaximumBatchSize ||
            idempotencyKey?.Trim().Length > 200 || files.Any(file => file is null))
            return Result.Failure<IReadOnlyList<UploadFileResultDto>>(
                Error.Validation("InvalidUploadBatch", "Danh sách file hoặc Idempotency-Key không hợp lệ."));

        var results = new List<UploadFileResultDto>(files.Count);
        for (int index = 0; index < files.Count; index++)
        {
            UploadMediaRequest file = files[index] with { BatchSize = files.Count };
            string? fileKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? null
                : $"batch:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{idempotencyKey.Trim()}:{index}")))}";
            Result<MediaCompletedDto> result = await UploadAsync(
                authenticatedUserId, fileKey, file, cancellationToken);
            results.Add(new UploadFileResultDto(index, file.FileName,
                result.IsSuccess ? result.Value : null,
                result.IsFailure ? new UploadFileErrorDto(result.Error.Code, result.Error.Message) : null));
        }
        return Result.Success<IReadOnlyList<UploadFileResultDto>>(results);
    }

    public async Task<Result<MediaCompletedDto>> UploadAsync(
        string authenticatedUserId,
        string? idempotencyKey,
        UploadMediaRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Unauthorized("UnauthorizedUser", "Yêu cầu phải được xác thực từ một người dùng hợp lệ."));
        }

        if (idempotencyKey?.Trim().Length > 200)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("InvalidIdempotencyKey", "Idempotency-Key không được vượt quá 200 ký tự."));
        }

        long? maximumSize = MediaFilePolicy.MaxSizeBytesFor(request.ContentType);
        if (maximumSize is null)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("UnsupportedContentType", "Định dạng tệp không được hỗ trợ."));
        }

        if (request.BatchSize is < 1 or > UploadMediaRequest.MaximumBatchSize ||
            string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 1024 ||
            string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 100 ||
            request.Content is null)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("InvalidUploadInput", "Thông tin file không hợp lệ."));
        }

        if (!MediaFilePolicy.IsSizeAllowed(request.ContentType, request.SizeBytes))
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("FileSizeExceeded", "Dung lượng tệp không hợp lệ hoặc vượt giới hạn cho phép."));
        }

        if (request.Sha256Checksum is { Length: > 0 } checksum &&
            (checksum.Trim().Length != 64 || !checksum.Trim().All(Uri.IsHexDigit)))
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("InvalidChecksum", "Checksum SHA-256 phải gồm 64 ký tự hex."));
        }

        // Chỉ giữ tối đa giới hạn của định dạng file trong bộ nhớ. Hash nội dung giúp phát hiện
        // cùng Idempotency-Key nhưng khác binary, kể cả khi tên và dung lượng giống nhau.
        await using var buffer = new MemoryStream((int)request.SizeBytes);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await request.Content.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
            {
                if (buffer.Length + read > maximumSize.Value)
                {
                    return Result.Failure<MediaCompletedDto>(
                        Error.Validation("FileSizeExceeded", "Dung lượng tệp vượt giới hạn cho phép."));
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length != request.SizeBytes)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("FileSizeMismatch", "Dung lượng file không khớp với nội dung nhận được."));
        }

        string fileHash = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            .ToLowerInvariant();
        string payloadHash = ComputePayloadHash(request, fileHash);
        string caller = authenticatedUserId.Trim();
        string key = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        MediaUploadSession? session = await _sessionRepository.FindByCallerAndIdempotencyKeyAsync(
            caller, key, cancellationToken);
        if (session is not null)
        {
            if (!string.Equals(session.RequestPayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return Result.Failure<MediaCompletedDto>(
                    Error.Conflict("IdempotencyConflict", "Idempotency-Key đã dùng cho file hoặc thông tin khác."));
            }

            if (session.Status == UploadSessionStatus.Completed)
            {
                MediaAsset? completed = session.MediaAsset ??
                    await _assetRepository.GetByIdAsync(session.MediaAssetId, cancellationToken);
                return completed is null
                    ? Result.Failure<MediaCompletedDto>(Error.Failure("MissingMedia", "Không tìm thấy Media đã tải."))
                    : await AddPreviewUrlAsync(caller, ToCompletedDto(completed), cancellationToken);
            }

            if (session.Status != UploadSessionStatus.Pending || now >= session.ExpiresAtUtc)
            {
                return Result.Failure<MediaCompletedDto>(
                    Error.Conflict("UploadNotRetryable", "Lần tải trước đã hết hạn hoặc bị từ chối. Hãy dùng Idempotency-Key mới."));
            }
        }
        else
        {
            Guid mediaId = Guid.NewGuid();
            Guid uploadId = Guid.NewGuid();
            MediaAsset asset = new()
            {
                Id = mediaId,
                Status = MediaStatus.PendingUpload,
                Visibility = MediaVisibility.Private,
                OriginalFileName = FileNameSanitizer.Sanitize(request.FileName),
                DeclaredContentType = request.ContentType.Trim().ToLowerInvariant(),
                DeclaredSizeBytes = request.SizeBytes,
                DeclaredChecksumSha256 = string.IsNullOrWhiteSpace(request.Sha256Checksum)
                    ? null : request.Sha256Checksum.Trim().ToLowerInvariant(),
                CreatedByUserId = caller,
                CreatedAtUtc = now
            };
            session = new MediaUploadSession
            {
                Id = uploadId,
                MediaAssetId = mediaId,
                CallerIdentity = caller,
                IdempotencyKey = key,
                RequestPayloadHash = payloadHash,
                TemporaryStorageKey = $"temp/uploads/{uploadId:N}/{Guid.NewGuid():N}.tmp",
                Status = UploadSessionStatus.Pending,
                ExpiresAtUtc = now.Add(_sessionLifetime),
                CreatedAtUtc = now,
                MediaAsset = asset
            };

            await _assetRepository.AddAsync(asset, cancellationToken);
            await _sessionRepository.AddAsync(session, cancellationToken);
            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (MediaWriteConflictException)
            {
                return Result.Failure<MediaCompletedDto>(
                    Error.Conflict("UploadInProgress", "Yêu cầu tải tệp đang được xử lý. Vui lòng thử lại."));
            }
        }

        buffer.Position = 0;
        try
        {
            await _objectStorageService.PutObjectAsync(
                session.TemporaryStorageKey, buffer, buffer.Length,
                request.ContentType.Trim().ToLowerInvariant(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUploadFailure(_logger, exception.GetType().Name, session.Id);
            return Result.Failure<MediaCompletedDto>(
                Error.Failure("StorageUnavailable", "Không thể tải file lên hệ thống. Vui lòng thử lại."));
        }

        Result<MediaCompletedDto> finalized = await FinalizeStoredUploadAsync(caller, session.Id, cancellationToken);
        return finalized.IsFailure
            ? finalized
            : await AddPreviewUrlAsync(caller, finalized.Value, cancellationToken);
    }

    private async Task<Result<MediaCompletedDto>> AddPreviewUrlAsync(
        string caller, MediaCompletedDto completed, CancellationToken cancellationToken)
    {
        if (!string.Equals(completed.Status, MediaStatus.Ready.ToString(), StringComparison.Ordinal))
        {
            return Result.Success(completed);
        }

        try
        {
            Result<MediaReadUrlDto> preview = await _accessService.CreateUploaderPreviewUrlAsync(
                caller, completed.MediaId, cancellationToken);
            if (preview.IsSuccess)
            {
                return Result.Success(completed with
                {
                    PreviewUrl = preview.Value.Url,
                    PreviewUrlExpiresAtUtc = preview.Value.ExpiresAtUtc
                });
            }

            LogPreviewFailure(_logger, completed.MediaId, preview.Error.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogPreviewFailure(_logger, completed.MediaId, exception.GetType().Name);
        }

        // File đã Ready; lỗi ký URL không được biến upload thành lỗi để client gửi lại file.
        return Result.Success(completed);
    }

    private static MediaCompletedDto ToCompletedDto(MediaAsset asset) => new(
        asset.Id, asset.Status.ToString(), asset.OriginalFileName,
        asset.ActualContentType ?? asset.DeclaredContentType,
        asset.ActualSizeBytes ?? asset.DeclaredSizeBytes);

    private static string ComputePayloadHash(UploadMediaRequest request, string fileHash)
    {
        string raw = JsonSerializer.Serialize(new object?[]
        {
            request.FileName?.Trim(), request.ContentType?.Trim().ToLowerInvariant(),
            request.SizeBytes, request.Sha256Checksum?.Trim().ToLowerInvariant(), fileHash,
            request.BatchSize
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to store upload for session {SessionId}: {ErrorType}")]
    private static partial void LogUploadFailure(ILogger logger, string errorType, Guid sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Could not create upload preview URL for media {MediaId}: {ErrorCode}")]
    private static partial void LogPreviewFailure(ILogger logger, Guid mediaId, string errorCode);
}
