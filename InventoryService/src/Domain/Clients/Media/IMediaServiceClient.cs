using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Domain.Clients.Media;

/// <summary>
/// Client tích hợp với MediaService cho việc xác thực và công khai media.
/// </summary>
public interface IMediaServiceClient
{
    /// <summary>
    /// Thẩm tra trạng thái sẵn sàng của media asset tại MediaService.
    /// </summary>
    Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đánh dấu media asset là Public sau khi nghiệp vụ hoàn tất thành công.
    /// </summary>
    Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default);
}
