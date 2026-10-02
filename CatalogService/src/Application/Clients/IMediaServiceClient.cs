using Dprental.CatalogService.Application.DTOs.Media;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Clients;

/// <summary>
/// Client giao tiếp nội bộ với MediaService phục vụ kiểm tra và cập nhật visibility.
/// </summary>
public interface IMediaServiceClient
{
    /// <summary>
    /// Xác minh media asset đang tồn tại và lấy thông tin trạng thái, người upload, định dạng.
    /// </summary>
    Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đổi trạng thái hiển thị của media asset trên MediaService.
    /// </summary>
    Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default);
}
