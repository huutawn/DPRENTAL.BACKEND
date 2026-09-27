using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;

namespace Dprental.MediaService.Application.IServices;

/// <summary>
/// Service nhận file và chỉ trả MediaId sau khi file đã được xác minh.
/// </summary>
public interface IMediaUploadService
{
    /// <summary>
    /// Upload file trong một request; phiên nội bộ chỉ phục vụ idempotency và dọn dẹp.
    /// </summary>
    Task<Result<MediaCompletedDto>> UploadAsync(
        string authenticatedUserId,
        string? idempotencyKey,
        UploadMediaRequest request,
        CancellationToken cancellationToken = default);
}
