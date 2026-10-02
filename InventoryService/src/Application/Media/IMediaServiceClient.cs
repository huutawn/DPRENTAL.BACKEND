using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Media;

/// <summary>
/// DTO chứa thông tin kết quả xác minh media từ MediaService.
/// </summary>
public sealed record MediaVerificationInfo(
    Guid MediaId,
    string Status,
    string UploadedByUserId
);

/// <summary>
/// DTO chứa thông tin kết quả cập nhật visibility từ MediaService.
/// </summary>
public sealed record MediaVisibilityInfo(
    Guid MediaId,
    string Visibility
);

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
