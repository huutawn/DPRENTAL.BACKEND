using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common;

namespace Dprental.MediaService.Application.IServices;

/// <summary>
/// Service nhận file và chỉ trả MediaId sau khi file đã được xác minh.
/// </summary>
public interface IMediaUploadService
{
    Task<Result<IReadOnlyList<UploadFileResultDto>>> UploadManyAsync(
        string authenticatedUserId,
        string? idempotencyKey,
        IReadOnlyList<UploadMediaRequest> files,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upload file trong một request; phiên nội bộ chỉ phục vụ idempotency và dọn dẹp.
    /// </summary>
    Task<Result<MediaCompletedDto>> UploadAsync(
        string authenticatedUserId,
        string? idempotencyKey,
        UploadMediaRequest request,
        CancellationToken cancellationToken = default);
}
