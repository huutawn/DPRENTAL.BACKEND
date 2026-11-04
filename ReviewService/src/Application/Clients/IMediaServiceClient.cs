using Dprental.ReviewService.Application.DTOs.Media;
using Dprental.ReviewService.Common;

namespace Dprental.ReviewService.Application.Clients;

/// <summary>
/// Hợp đồng client HTTP gọi sang MediaService để xác thực và cấp quyền truy cập media.
/// </summary>
public interface IMediaServiceClient
{
    /// <summary>
    /// Xác minh sự tồn tại, trạng thái xử lý và quyền sở hữu của media.
    /// </summary>
    Task<Result<MediaVerifyDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yêu cầu sinh URL truy cập đọc có thời hạn ngắn (tối đa 60 giây).
    /// </summary>
    Task<Result<MediaAccessDto>> CreatePrivateReadAccessAsync(Guid mediaId, CancellationToken cancellationToken = default);
}
