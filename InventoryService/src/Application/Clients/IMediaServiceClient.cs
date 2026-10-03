using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Clients;

/// <summary>
/// Hợp đồng client gọi tới MediaService nội bộ theo chuẩn Server-to-Server.
/// </summary>
public interface IMediaServiceClient
{
    /// <summary>
    /// Xác minh thông tin media (ID, status, uploader) trước khi liên kết với entity nghiệp vụ.
    /// </summary>
    Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đặt trạng thái visibility của media thành Public.
    /// </summary>
    Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default);
}
