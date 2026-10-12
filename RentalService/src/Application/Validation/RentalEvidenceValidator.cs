using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Validation;

/// <summary>
/// Enforces frozen evidence attachment validation rules across Handover, Return, Deduction, and Dispute workflows.
/// Invariants:
/// - Maximum 10 media IDs per evidence collection.
/// - No empty GUIDs and no duplicate IDs.
/// - All media must exist in MediaService and be in 'Ready' status.
/// - All media must be 'Private' visibility (checked via metadata endpoint; Public is strictly rejected).
/// - UploadedByUserId (checked via verify endpoint) must strictly match the actor submitting the evidence.
/// - Response MediaId from both endpoints must strictly match the requested media ID.
/// - Atomic: If any media fails verification or metadata check, the entire evidence batch is rejected.
/// - Propagates CancellationToken.
/// </summary>
public static class RentalEvidenceValidator
{
    public static async Task<Result<IReadOnlyList<VerifiedEvidenceMediaDto>>> ValidateAndVerifyEvidenceAsync(
        IEnumerable<Guid>? mediaIds,
        Guid expectedUploaderId,
        IRentalMediaClient mediaClient,
        bool isRequired = false,
        CancellationToken ct = default)
    {
        var idList = mediaIds?.ToList() ?? [];

        if (idList.Count == 0)
        {
            if (isRequired)
            {
                return Error.Validation(
                    MediaContractSpec.ErrorCodes.InvalidMediaId,
                    "Yêu cầu đính kèm ít nhất một hình ảnh/video bằng chứng.");
            }

            return Result.Success<IReadOnlyList<VerifiedEvidenceMediaDto>>([]);
        }

        if (idList.Count > MediaContractSpec.MaxEvidenceMediaCount)
        {
            return Error.Validation(
                MediaContractSpec.ErrorCodes.MediaLimitExceeded,
                $"Số lượng bằng chứng vượt quá giới hạn tối đa ({MediaContractSpec.MaxEvidenceMediaCount} tệp).");
        }

        if (idList.Any(id => id == Guid.Empty))
        {
            return Error.Validation(
                MediaContractSpec.ErrorCodes.InvalidMediaId,
                "Danh sách bằng chứng chứa mã định danh (MediaId) không hợp lệ.");
        }

        if (idList.Distinct().Count() != idList.Count)
        {
            return Error.Conflict(
                MediaContractSpec.ErrorCodes.DuplicateMediaId,
                "Danh sách bằng chứng chứa mã định danh (MediaId) bị trùng lặp.");
        }

        List<VerifiedEvidenceMediaDto> verifiedItems = new(idList.Count);

        foreach (var mediaId in idList)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Verify existence, status, and uploader via GET /api/v1/media/{id}/verify
            Result<MediaVerifyDto> verifyResult = await mediaClient.VerifyMediaAsync(mediaId, ct);
            if (verifyResult.IsFailure)
            {
                return verifyResult.Error;
            }

            MediaVerifyDto verify = verifyResult.Value;

            if (verify.MediaId != mediaId)
            {
                return Error.Conflict(
                    MediaContractSpec.ErrorCodes.MediaIdMismatch,
                    $"Mã MediaId trong phản hồi xác thực '{verify.MediaId}' không khớp với yêu cầu '{mediaId}'.");
            }

            if (!string.Equals(verify.Status, MediaContractSpec.Statuses.Ready, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict(
                    MediaContractSpec.ErrorCodes.MediaNotReady,
                    $"Tệp bằng chứng '{mediaId}' chưa sẵn sàng (trạng thái hiện tại: {verify.Status}).");
            }

            if (!Guid.TryParse(verify.UploadedByUserId, out Guid uploaderGuid) || uploaderGuid != expectedUploaderId)
            {
                return Error.Forbidden(
                    MediaContractSpec.ErrorCodes.MediaUploaderMismatch,
                    $"Tệp bằng chứng '{mediaId}' không do người thực hiện thao tác tải lên.");
            }

            // 2. Verify visibility via GET /api/v1/media/{id} metadata route
            Result<MediaMetadataDto> metadataResult = await mediaClient.GetMetadataAsync(mediaId, ct);
            if (metadataResult.IsFailure)
            {
                return metadataResult.Error;
            }

            MediaMetadataDto meta = metadataResult.Value;

            if (meta.MediaId != mediaId)
            {
                return Error.Conflict(
                    MediaContractSpec.ErrorCodes.MediaIdMismatch,
                    $"Mã MediaId trong phản hồi metadata '{meta.MediaId}' không khớp với yêu cầu '{mediaId}'.");
            }

            if (!string.Equals(meta.Status, MediaContractSpec.Statuses.Ready, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict(
                    MediaContractSpec.ErrorCodes.MediaNotReady,
                    $"Tệp bằng chứng '{mediaId}' chưa sẵn sàng trong metadata (trạng thái: {meta.Status}).");
            }

            if (!string.Equals(meta.Visibility, MediaContractSpec.Visibilities.Private, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Validation(
                    MediaContractSpec.ErrorCodes.MediaMustBePrivate,
                    $"Tệp bằng chứng '{mediaId}' có chế độ hiển thị '{meta.Visibility}'. Tất cả bằng chứng bắt buộc phải ở chế độ 'Private'.");
            }

            verifiedItems.Add(new VerifiedEvidenceMediaDto(
                mediaId,
                uploaderGuid,
                verify.Status,
                meta.Visibility,
                verify.OriginalFileName,
                verify.ContentType));
        }

        return Result.Success<IReadOnlyList<VerifiedEvidenceMediaDto>>(verifiedItems);
    }
}
