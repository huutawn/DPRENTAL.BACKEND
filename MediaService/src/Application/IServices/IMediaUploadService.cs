using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;

namespace Dprental.MediaService.Application.IServices;

/// <summary>
/// Service xử lý toàn bộ logic nghiệp vụ tải tệp media: khởi tạo phiên upload, hoàn tất kiểm tra, và xác minh nội bộ.
/// </summary>
public interface IMediaUploadService
{
    /// <summary>
    /// Client dùng user token để khởi tạo phiên tải tệp trực tiếp lên MinIO.
    /// </summary>
    Task<Result<UploadSessionDto>> InitializeUploadAsync(
        string authenticatedUserId,
        string idempotencyKey,
        InitializeUploadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Client xác nhận đã hoàn tất upload binary, MediaService tiến hành kiểm tra object thực tế và chuyển trạng thái Ready.
    /// </summary>
    Task<Result<MediaCompletedDto>> CompleteUploadAsync(
        string authenticatedUserId,
        Guid uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// API nội bộ để Business Service kiểm tra trạng thái Ready, purpose, ownerEntityId, và người upload trước khi lưu tham chiếu.
    /// </summary>
    Task<Result<MediaVerificationDto>> VerifyMediaAsync(
        string authenticatedServiceName,
        Guid mediaId,
        CancellationToken cancellationToken = default);
}
