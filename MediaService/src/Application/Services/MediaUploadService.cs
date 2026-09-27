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
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<MediaUploadService> _logger;
    private readonly TimeSpan _sessionLifetime;

    public MediaUploadService(
        IMediaUploadSessionRepository sessionRepository,
        IMediaAssetRepository assetRepository,
        IMediaCleanupRepository cleanupRepository,
        IObjectStorageService objectStorageService,
        IUnitOfWork unitOfWork,
        IOptions<UploadSettings> settings,
        ILogger<MediaUploadService> logger)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _cleanupRepository = cleanupRepository ?? throw new ArgumentNullException(nameof(cleanupRepository));
        _objectStorageService = objectStorageService ?? throw new ArgumentNullException(nameof(objectStorageService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionLifetime = TimeSpan.FromMinutes(settings?.Value.SessionLifetimeMinutes
            ?? throw new ArgumentNullException(nameof(settings)));
    }

    public async Task<Result<UploadSessionDto>> InitializeUploadAsync(
        string authenticatedUserId,
        string idempotencyKey,
        InitializeUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Unauthorized("UnauthorizedUser", "Yêu cầu phải được xác thực từ một người dùng hợp lệ."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingIdempotencyKey", "Idempotency-Key không được để trống."));
        }

        if (idempotencyKey.Trim().Length > 200)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("InvalidIdempotencyKey", "Idempotency-Key không được vượt quá 200 ký tự."));
        }

        if (string.IsNullOrWhiteSpace(request.Purpose))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingPurpose", "Mục đích sử dụng (purpose) không được để trống."));
        }

        var policy = MediaPurposePolicy.GetPolicy(request.Purpose);
        if (policy == null)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("UnsupportedPurpose", $"Mục đích sử dụng '{request.Purpose}' chưa được cấu hình hoặc không được hỗ trợ."));
        }

        if (string.IsNullOrWhiteSpace(request.OwnerEntityId) || request.OwnerEntityId.Trim().Length > 100)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingOwnerEntityId", "ID đối tượng nghiệp vụ dự kiến (ownerEntityId) không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 1024)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingFileName", "Tên tệp không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(request.ContentType) || request.ContentType.Length > 100)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingContentType", "Loại nội dung (contentType) không được để trống."));
        }

        if (request.SizeBytes <= 0)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("InvalidFileSize", "Dung lượng tệp phải lớn hơn 0."));
        }

        if (!policy.IsContentTypeAllowed(request.ContentType))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("UnsupportedContentType", "Định dạng tệp không được hỗ trợ cho mục đích này."));
        }

        if (!policy.IsSizeAllowed(request.SizeBytes))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("FileSizeExceeded", "Dung lượng tệp vượt quá giới hạn cho phép."));
        }

        if (request.Sha256Checksum is { Length: > 0 } checksum &&
            (checksum.Trim().Length != 64 || !checksum.Trim().All(Uri.IsHexDigit)))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("InvalidChecksum", "Checksum SHA-256 phải gồm 64 ký tự hex."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string caller = authenticatedUserId.Trim();
        string key = idempotencyKey.Trim();

        string payloadHash = ComputePayloadHash(request);

        // 1. Kiểm tra Idempotency cho cặp (caller, idempotencyKey)
        var existingSession = await _sessionRepository.FindByCallerAndIdempotencyKeyAsync(caller, key, cancellationToken);
        if (existingSession != null)
        {
            return await ResumeExistingSessionAsync(existingSession, payloadHash, now, cancellationToken);
        }

        // 2. Khởi tạo MediaAsset và MediaUploadSession
        // service sở hữu và loại đối tượng được xác định từ cấu hình purpose, không nhận từ client
        string sanitizedFileName = FileNameSanitizer.Sanitize(request.FileName);
        Guid mediaId = Guid.NewGuid();
        Guid uploadId = Guid.NewGuid();
        DateTimeOffset sessionExpiresAtUtc = now.Add(_sessionLifetime);
        string tempStorageKey = $"temp/uploads/{uploadId:N}/{Guid.NewGuid():N}.tmp";

        var mediaAsset = new MediaAsset
        {
            Id = mediaId,
            OwnerService = policy.OwnerService,
            OwnerEntityType = policy.OwnerEntityType,
            OwnerEntityId = request.OwnerEntityId.Trim(),
            Purpose = policy.Purpose,
            Status = MediaStatus.PendingUpload,
            Visibility = MediaVisibility.Private,
            OriginalFileName = sanitizedFileName,
            DeclaredContentType = request.ContentType.Trim().ToLowerInvariant(),
            DeclaredSizeBytes = request.SizeBytes,
            DeclaredChecksumSha256 = string.IsNullOrWhiteSpace(request.Sha256Checksum) ? null : request.Sha256Checksum.Trim().ToLowerInvariant(),
            CreatedByUserId = caller,
            CreatedAtUtc = now,
            Version = 1
        };

        var uploadSession = new MediaUploadSession
        {
            Id = uploadId,
            MediaAssetId = mediaId,
            CallerIdentity = caller,
            IdempotencyKey = key,
            RequestPayloadHash = payloadHash,
            TemporaryStorageKey = tempStorageKey,
            Status = UploadSessionStatus.Pending,
            ExpiresAtUtc = sessionExpiresAtUtc,
            CreatedAtUtc = now,
            Version = 1
        };

        await _assetRepository.AddAsync(mediaAsset, cancellationToken);
        await _sessionRepository.AddAsync(uploadSession, cancellationToken);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (MediaWriteConflictException)
        {
            existingSession = await _sessionRepository.FindByCallerAndIdempotencyKeyAsync(caller, key, cancellationToken);
            return existingSession is null
                ? Result.Failure<UploadSessionDto>(Error.Conflict("UploadConflict", "Yêu cầu tải tệp đang được xử lý. Vui lòng thử lại."))
                : await ResumeExistingSessionAsync(existingSession, payloadHash, now, cancellationToken);
        }

        // Sinh URL ký sẵn sau khi metadata được lưu; không kéo dài hạn phiên.
        TimeSpan remaining = sessionExpiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Expired("SessionExpired", "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."));
        }

        var presigned = await _objectStorageService.GeneratePresignedUploadUrlAsync(
            tempStorageKey,
            mediaAsset.DeclaredContentType,
            remaining,
            cancellationToken);

        var responseDto = new UploadSessionDto(
            mediaId,
            uploadId,
            presigned.UploadUrl,
            presigned.Method,
            presigned.RequiredHeaders,
            sessionExpiresAtUtc);

        return Result.Success(responseDto);
    }

    public async Task<Result<MediaVerificationDto>> VerifyMediaAsync(
        string authenticatedServiceName,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedServiceName))
        {
            return Result.Failure<MediaVerificationDto>(
                Error.Unauthorized("UnauthorizedService", "Yêu cầu phải được xác thực từ một service hợp lệ."));
        }

        if (mediaId == Guid.Empty)
        {
            return Result.Failure<MediaVerificationDto>(
                Error.Validation("InvalidMediaId", "Mã media (mediaId) không hợp lệ."));
        }

        var asset = await _assetRepository.GetByIdAsync(mediaId, cancellationToken);
        if (asset == null)
        {
            return Result.Failure<MediaVerificationDto>(
                Error.NotFound("MediaNotFound", "Không tìm thấy thông tin Media."));
        }

        if (!string.Equals(asset.OwnerService, authenticatedServiceName, StringComparison.Ordinal))
        {
            return Result.Failure<MediaVerificationDto>(
                Error.NotFound("MediaNotFound", "Không tìm thấy thông tin Media."));
        }

        if (asset.Status != MediaStatus.Ready)
        {
            return Result.Failure<MediaVerificationDto>(
                Error.Conflict("MediaNotReady", "Media chưa sẵn sàng để sử dụng."));
        }

        var dto = new MediaVerificationDto(
            asset.Id,
            asset.Status.ToString(),
            asset.OwnerService,
            asset.OwnerEntityType,
            asset.OwnerEntityId,
            asset.Purpose,
            asset.CreatedByUserId,
            asset.OriginalFileName,
            asset.ActualContentType ?? asset.DeclaredContentType,
            asset.ActualSizeBytes ?? asset.DeclaredSizeBytes,
            asset.CreatedAtUtc);

        return Result.Success(dto);
    }

    private async Task<Result<UploadSessionDto>> ResumeExistingSessionAsync(
        MediaUploadSession session,
        string payloadHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(session.RequestPayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Conflict("IdempotencyConflict", "Yêu cầu tải tệp bị xung đột."));
        }

        if (session.Status == UploadSessionStatus.Completed)
        {
            return Result.Success(new UploadSessionDto(
                session.MediaAssetId, session.Id, null, null,
                new Dictionary<string, string>(), session.ExpiresAtUtc, "Completed"));
        }

        if (session.Status == UploadSessionStatus.Rejected)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Unprocessable("FileRejected", "Tệp không hợp lệ hoặc không đáp ứng yêu cầu."));
        }

        TimeSpan remaining = session.ExpiresAtUtc - now;
        if (session.Status == UploadSessionStatus.Expired || remaining <= TimeSpan.Zero)
        {
            return Result.Failure<UploadSessionDto>(
                Error.Expired("SessionExpired", "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."));
        }

        var presigned = await _objectStorageService.GeneratePresignedUploadUrlAsync(
            session.TemporaryStorageKey,
            session.MediaAsset!.DeclaredContentType,
            remaining,
            cancellationToken);

        return Result.Success(new UploadSessionDto(
            session.MediaAssetId, session.Id, presigned.UploadUrl,
            presigned.Method, presigned.RequiredHeaders, session.ExpiresAtUtc));
    }

    private static string ComputePayloadHash(InitializeUploadRequest request)
    {
        string raw = JsonSerializer.Serialize(new object?[]
        {
            request.Purpose?.Trim().ToLowerInvariant(),
            request.OwnerEntityId?.Trim(),
            request.FileName?.Trim(),
            request.ContentType?.Trim().ToLowerInvariant(),
            request.SizeBytes,
            request.Sha256Checksum?.Trim().ToLowerInvariant()
        });
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
