using System.Security.Cryptography;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Common;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.MediaService.Application.Services;

public partial class MediaUploadService
{
    private async Task<Result<MediaCompletedDto>> FinalizeStoredUploadAsync(
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

        // Chỉ thao tác upload đang xử lý cho caller đã tạo phiên nội bộ.
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
        // Kiểm tra kích thước trước khi nạp object vào bộ nhớ.
        var sourceMetadata = await _objectStorageService.GetObjectMetadataAsync(session.TemporaryStorageKey, cancellationToken);
        if (sourceMetadata == null)
        {
            return Result.Failure<MediaCompletedDto>(
                Error.Validation("ObjectNotFound", "Tệp chưa được tải lên hoặc tải lên chưa hoàn tất."));
        }

        if (!MediaFilePolicy.IsSizeAllowed(asset.DeclaredContentType, sourceMetadata.ContentLength))
        {
            const string reason = "Dung lượng tệp vượt quá giới hạn cho phép.";
            await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken);
            return Result.Failure<MediaCompletedDto>(Error.Unprocessable("FileSizeExceeded", reason));
        }

        // Mỗi lần retry tạo một bản sao riêng để không đọc object tạm đang được
        // request đồng thời ghi lại. Chỉ bản sao đã xác minh mới trở thành Ready.
        string permanentKey = $"media/{asset.Id:N}/{Guid.NewGuid():N}.bin";
        try
        {
            await _objectStorageService.CopyObjectAsync(session.TemporaryStorageKey, permanentKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeleteCopiedObjectBestEffortAsync(permanentKey);
            throw;
        }
        catch
        {
            await DeleteCopiedObjectBestEffortAsync(permanentKey);
            return Result.Failure<MediaCompletedDto>(
                Error.Failure("StorageError", "Không thể lưu trữ tệp vào hệ thống. Vui lòng thử lại."));
        }

        try
        {
            var finalMetadata = await _objectStorageService.GetObjectMetadataAsync(permanentKey, cancellationToken);
            if (finalMetadata == null || !MediaFilePolicy.IsSizeAllowed(asset.DeclaredContentType, finalMetadata.ContentLength))
            {
                const string reason = "Dung lượng tệp vượt quá giới hạn cho phép.";
                await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken, permanentKey);
                return Result.Failure<MediaCompletedDto>(Error.Unprocessable("FileSizeExceeded", reason));
            }

            await using Stream? stream = await _objectStorageService.GetObjectStreamAsync(permanentKey, cancellationToken);
            if (stream == null || stream.Length == 0)
            {
                await MarkSessionAndAssetRejectedAsync(session, asset, "Tệp không có nội dung.", now, cancellationToken, permanentKey);
                return Result.Failure<MediaCompletedDto>(
                    Error.Unprocessable("EmptyFile", "Tệp không có nội dung."));
            }

            long actualLength = stream.Length;

            if (!MediaFilePolicy.IsSizeAllowed(asset.DeclaredContentType, actualLength))
            {
                string reason = "Dung lượng tệp vượt quá giới hạn cho phép.";
                await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken, permanentKey);
                return Result.Failure<MediaCompletedDto>(
                    Error.Unprocessable("FileSizeExceeded", reason));
            }

            string? detectedContentType = await FileSignatureDetector.DetectContentTypeAsync(stream, cancellationToken);
            if (detectedContentType == null || MediaFilePolicy.MaxSizeBytesFor(detectedContentType) is null)
            {
                string reason = "Định dạng tệp không được hỗ trợ.";
                await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken, permanentKey);
                return Result.Failure<MediaCompletedDto>(
                    Error.Unprocessable("UnsupportedContentType", reason));
            }

            if (!string.Equals(detectedContentType, asset.DeclaredContentType, StringComparison.OrdinalIgnoreCase))
            {
                string reason = "Loại tệp thực tế không khớp với thông tin đã khai báo.";
                await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken, permanentKey);
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
                await MarkSessionAndAssetRejectedAsync(session, asset, reason, now, cancellationToken, permanentKey);
                return Result.Failure<MediaCompletedDto>(
                    Error.Unprocessable("ChecksumMismatch", reason));
            }

            // Ghi trạng thái và công việc dọn khóa tạm trong cùng một lần SaveChanges.
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
            await _cleanupRepository.AddAsync(new MediaCleanupItem
            {
                Id = Guid.NewGuid(),
                StorageKey = session.TemporaryStorageKey,
                Reason = "UploadCompleted",
                Status = CleanupItemStatus.Pending,
                CreatedAtUtc = now,
                EligibleAtUtc = session.ExpiresAtUtc.AddMinutes(1)
            }, cancellationToken);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(new MediaCompletedDto(
                asset.Id,
                asset.Status.ToString(),
                asset.OriginalFileName,
                detectedContentType,
                actualLength));
        }
        catch (MediaWriteConflictException)
        {
            await DeleteCopiedObjectBestEffortAsync(permanentKey);
            var reloadedSession = await _sessionRepository.GetByIdUntrackedAsync(uploadId, cancellationToken);
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

            return Result.Failure<MediaCompletedDto>(
                Error.Conflict("UploadInProgress", "Phiên tải tệp đang được hoàn tất. Vui lòng thử lại."));
        }
        catch
        {
            await DeleteCopiedObjectBestEffortAsync(permanentKey);
            throw;
        }
    }

    private async Task DeleteCopiedObjectBestEffortAsync(string storageKey)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _objectStorageService.DeleteObjectAsync(storageKey, timeout.Token);
        }
        catch (Exception exception)
        {
            LogCopyCleanupFailure(_logger, exception, storageKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove upload copy {StorageKey}")]
    private static partial void LogCopyCleanupFailure(ILogger logger, Exception exception, string storageKey);

    private async Task MarkSessionAndAssetRejectedAsync(
        MediaUploadSession session,
        MediaAsset asset,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? copiedStorageKey = null)
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
            CreatedAtUtc = now,
            EligibleAtUtc = session.ExpiresAtUtc.AddMinutes(1)
        }, cancellationToken);

        if (copiedStorageKey is not null)
        {
            await _cleanupRepository.AddAsync(new MediaCleanupItem
            {
                Id = Guid.NewGuid(),
                StorageKey = copiedStorageKey,
                Reason = "RejectedCopy",
                Status = CleanupItemStatus.Pending,
                CreatedAtUtc = now
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

}
