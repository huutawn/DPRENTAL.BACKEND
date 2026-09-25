using System.Security.Cryptography;
using System.Text;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.EntityFrameworkCore;

namespace Dprental.MediaService.Application.Services;

public class MediaUploadService : IMediaUploadService
{
    private readonly IMediaUploadSessionRepository _sessionRepository;
    private readonly IMediaAssetRepository _assetRepository;
    private readonly IMediaCleanupRepository _cleanupRepository;
    private readonly IObjectStorageService _objectStorageService;
    private readonly IUnitOfWork _unitOfWork;

    public MediaUploadService(
        IMediaUploadSessionRepository sessionRepository,
        IMediaAssetRepository assetRepository,
        IMediaCleanupRepository cleanupRepository,
        IObjectStorageService objectStorageService,
        IUnitOfWork unitOfWork)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _assetRepository = assetRepository ?? throw new ArgumentNullException(nameof(assetRepository));
        _cleanupRepository = cleanupRepository ?? throw new ArgumentNullException(nameof(cleanupRepository));
        _objectStorageService = objectStorageService ?? throw new ArgumentNullException(nameof(objectStorageService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
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
                Error.Validation("MissingIdempotencyKey", "Tiêu đề hoặc khóa Idempotency-Key không được để trống."));
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

        if (string.IsNullOrWhiteSpace(request.OwnerEntityId))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingOwnerEntityId", "ID đối tượng nghiệp vụ dự kiến (ownerEntityId) không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return Result.Failure<UploadSessionDto>(
                Error.Validation("MissingFileName", "Tên tệp không được để trống."));
        }

        if (string.IsNullOrWhiteSpace(request.ContentType))
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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string caller = authenticatedUserId.Trim();
        string key = idempotencyKey.Trim();

        string payloadHash = ComputePayloadHash(request);

        // 1. Kiểm tra Idempotency cho cặp (caller, idempotencyKey)
        var existingSession = await _sessionRepository.FindByCallerAndIdempotencyKeyAsync(caller, key, cancellationToken);
        if (existingSession != null)
        {
            if (!string.Equals(existingSession.RequestPayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return Result.Failure<UploadSessionDto>(
                    Error.Conflict("IdempotencyConflict", "Yêu cầu tải tệp bị xung đột."));
            }

            if (existingSession.Status == UploadSessionStatus.Expired || now > existingSession.ExpiresAtUtc)
            {
                return Result.Failure<UploadSessionDto>(
                    Error.Expired("SessionExpired", "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."));
            }

            TimeSpan remaining = existingSession.ExpiresAtUtc - now;
            if (remaining <= TimeSpan.Zero)
            {
                return Result.Failure<UploadSessionDto>(
                    Error.Expired("SessionExpired", "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."));
            }

            var presignedResult = await _objectStorageService.GeneratePresignedUploadUrlAsync(
                existingSession.TemporaryStorageKey,
                existingSession.MediaAsset!.DeclaredContentType,
                remaining,
                cancellationToken);

            return Result.Success(new UploadSessionDto(
                existingSession.MediaAssetId,
                existingSession.Id,
                presignedResult.UploadUrl,
                presignedResult.Method,
                presignedResult.RequiredHeaders,
                existingSession.ExpiresAtUtc));
        }

        // 2. Khởi tạo MediaAsset và MediaUploadSession
        // service sở hữu và loại đối tượng được xác định từ cấu hình purpose, không nhận từ client
        string sanitizedFileName = FileNameSanitizer.Sanitize(request.FileName);
        Guid mediaId = Guid.NewGuid();
        Guid uploadId = Guid.NewGuid();
        DateTimeOffset sessionExpiresAtUtc = now.AddMinutes(15);
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
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // 3. Sinh URL ký sẵn PUT 15 phút (ngoài transaction DB)
        var presigned = await _objectStorageService.GeneratePresignedUploadUrlAsync(
            tempStorageKey,
            mediaAsset.DeclaredContentType,
            TimeSpan.FromMinutes(15),
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

    public async Task<Result<MediaCompletedDto>> CompleteUploadAsync(
        string authenticatedUserId,
        Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId))
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Unauthorized("UnauthorizedUser", "Yêu cầu phải được xác thực từ một người dùng hợp lệ."));
        }

        if (uploadId == Guid.Empty)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("InvalidUploadId", "Mã phiên upload (uploadId) không hợp lệ."));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string caller = authenticatedUserId.Trim();

        // 1. Tìm kiếm phiên upload
        var session = await _sessionRepository.GetByIdAsync(uploadId, cancellationToken);
        if (session == null)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.NotFound("SessionNotFound", "Phiên tải tệp không tồn tại."));
        }

        // 2. Kiểm tra quyền sở hữu phiên: chỉ người tạo phiên được gọi Complete
        if (!string.Equals(session.CallerIdentity, caller, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Forbidden("Forbidden", "Bạn không có quyền thao tác trên phiên tải tệp này."));
        }
        // 3. Xử lý trường hợp gọi lại khi phiên đã hoàn tất (Idempotent)
        if (session.Status == UploadSessionStatus.Completed)
        {
            var completedAsset = session.MediaAsset ?? await _assetRepository.GetByIdAsync(session.MediaAssetId, cancellationToken);
            if (completedAsset == null)
            {
                return Result.Failure<MediaCompletedDto>(
                    Error.NotFound("AssetNotFound", "Không tìm thấy thông tin Media liên kết."));
            }

            return Result.Success(new MediaCompletedDto(
                completedAsset.Id,
                completedAsset.Status.ToString(),
                completedAsset.OriginalFileName,
                completedAsset.ActualContentType ?? completedAsset.DeclaredContentType,
                completedAsset.ActualSizeBytes ?? completedAsset.DeclaredSizeBytes));
        }

        // 4. Kiểm tra phiên đã bị từ chối trước đó
        if (session.Status == UploadSessionStatus.Rejected)
        {
            var rejectedAsset = session.MediaAsset ?? await _assetRepository.GetByIdAsync(session.MediaAssetId, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("FileRejected", rejectedAsset?.RejectionReason ?? "Tệp không hợp lệ hoặc không đáp ứng yêu cầu."));
        }

        // 5. Kiểm tra phiên hết hạn
        if (session.Status == UploadSessionStatus.Expired || now > session.ExpiresAtUtc)
        {
            if (session.Status != UploadSessionStatus.Expired)
            {
                session.Status = UploadSessionStatus.Expired;
                session.CompletedAtUtc = now;
                await _sessionRepository.UpdateAsync(session, cancellationToken);

                await _cleanupRepository.AddAsync(new MediaCleanupItem
                {
                    Id = Guid.NewGuid(),
                    StorageKey = session.TemporaryStorageKey,
                    Reason = "SessionExpired",
                    Status = CleanupItemStatus.Pending,
                    CreatedAtUtc = now
                }, cancellationToken);

                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return Result.Failure<MediaCompletedDto>(
                Error.Expired("SessionExpired", "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."));
        }

        var asset = session.MediaAsset ?? await _assetRepository.GetByIdAsync(session.MediaAssetId, cancellationToken);
        if (asset == null)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.NotFound("AssetNotFound", "Không tìm thấy thông tin Media liên kết."));
        }
        // 6. Kiểm tra object thực tế trên MinIO (không giữ transaction DB)
        bool objectExists = await _objectStorageService.ObjectExistsAsync(session.TemporaryStorageKey, cancellationToken);
        if (!objectExists)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("ObjectNotFound", "Tệp chưa được tải lên hoặc tải lên chưa hoàn tất."));
        }

        // 7. Đọc stream object từ MinIO để xác minh dung lượng, magic bytes và checksum
        await using Stream? stream = await _objectStorageService.GetObjectStreamAsync(session.TemporaryStorageKey, cancellationToken);
        if (stream == null || stream.Length == 0)
        {
            await MarkSessionAndAssetRejectedAsync(session, asset, "Tệp không có nội dung.", now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("EmptyFile", "Tệp không có nội dung."));
        }

        long actualLength = stream.Length;

        var policy = MediaPurposePolicy.GetPolicy(asset.Purpose);
        if (policy == null || !policy.IsSizeAllowed(actualLength))
        {
            string reason = "Dung lượng tệp vượt quá giới hạn cho phép.";
            await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("FileSizeExceeded", reason));
        }

        string? detectedContentType = await FileSignatureDetector.DetectContentTypeAsync(stream, cancellationToken);
        if (detectedContentType == null || !policy.IsContentTypeAllowed(detectedContentType))
        {
            string reason = "Định dạng tệp không được hỗ trợ.";
            await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("UnsupportedContentType", reason));
        }

        if (!string.Equals(detectedContentType, asset.DeclaredContentType, StringComparison.OrdinalIgnoreCase))
        {
            string reason = "Loại tệp thực tế không khớp với thông tin đã khai báo.";
            await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("ContentTypeMismatch", reason));
        }

        stream.Position = 0;
        byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        string actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(asset.DeclaredChecksumSha256) &&
            !string.Equals(actualSha256, asset.DeclaredChecksumSha256, StringComparison.OrdinalIgnoreCase))
        {
            string reason = "Mã băm SHA-256 thực tế không khớp với checksum đã khai báo.";
            await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(
                Error.Unprocessable("ChecksumMismatch", reason));
        }

        // 8. Đưa bản hợp lệ sang khóa lưu trữ chính thức (service-only write key)
        string permanentKey = $"media/{asset.Id:N}/{Guid.NewGuid():N}.bin";

        try
        {
            await _objectStorageService.MoveObjectAsync(session.TemporaryStorageKey, permanentKey, cancellationToken);
        }
        catch (Exception ex)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Failure("StorageError", $"Không thể lưu trữ tệp vào hệ thống: {ex.Message}"));
        }

        // 9. Cập nhật trạng thái Ready và Completed trong cơ sở dữ liệu
        asset.Status = MediaStatus.Ready;
        asset.ActualContentType = detectedContentType;
        asset.ActualSizeBytes = actualLength;
        asset.ActualChecksumSha256 = actualSha256;
        asset.StorageKey = permanentKey;
        asset.UpdatedAtUtc = now;

        session.Status = UploadSessionStatus.Completed;
        session.CompletedAtUtc = now;

        await _assetRepository.UpdateAsync(asset, cancellationToken);
        await _sessionRepository.UpdateAsync(session, cancellationToken);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var reloadedSession = await _sessionRepository.GetByIdAsync(uploadId, cancellationToken);
            if (reloadedSession?.Status == UploadSessionStatus.Completed)
            {
                var reloadedAsset = reloadedSession.MediaAsset ?? await _assetRepository.GetByIdAsync(reloadedSession.MediaAssetId, cancellationToken);
                if (reloadedAsset != null)
                {
                    return Result.Success(new MediaCompletedDto(
                        reloadedAsset.Id,
                        reloadedAsset.Status.ToString(),
                        reloadedAsset.OriginalFileName,
                        reloadedAsset.ActualContentType ?? reloadedAsset.DeclaredContentType,
                        reloadedAsset.ActualSizeBytes ?? reloadedAsset.DeclaredSizeBytes));
                }
            }

            throw;
        }

        return Result.Success(new MediaCompletedDto(
            asset.Id,
            asset.Status.ToString(),
            asset.OriginalFileName,
            detectedContentType,
            actualLength));
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

    private async Task MarkSessionAndAssetRejectedAsync(
        MediaUploadSession session,
        MediaAsset asset,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        asset.Status = MediaStatus.Rejected;
        asset.RejectionReason = reason;
        asset.UpdatedAtUtc = now;

        session.Status = UploadSessionStatus.Rejected;
        session.CompletedAtUtc = now;

        await _assetRepository.UpdateAsync(asset, cancellationToken);
        await _sessionRepository.UpdateAsync(session, cancellationToken);

        await _cleanupRepository.AddAsync(new MediaCleanupItem
        {
            Id = Guid.NewGuid(),
            StorageKey = session.TemporaryStorageKey,
            Reason = $"Rejected: {reason}",
            Status = CleanupItemStatus.Pending,
            CreatedAtUtc = now
        }, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static string ComputePayloadHash(InitializeUploadRequest request)
    {
        string raw = $"{request.Purpose?.Trim()}|{request.OwnerEntityId?.Trim()}|{request.FileName?.Trim()}|{request.ContentType?.Trim().ToLowerInvariant()}|{request.SizeBytes}|{request.Sha256Checksum?.Trim().ToLowerInvariant()}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
