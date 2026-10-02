namespace Dprental.CatalogService.Application.Clients;

/// <summary>
/// Xác minh MediaId với Media Service trước khi Catalog lưu tham chiếu.
/// Catalog chỉ cần biết media có dùng được hay không, không nhận path, bucket hay URL.
/// </summary>
public interface IMediaVerificationClient
{
    /// <summary>
    /// Không ném exception cho lỗi hạ tầng; mọi trường hợp không xác minh được trả về
    /// <see cref="MediaVerificationStatus.Unavailable"/>.
    /// </summary>
    Task<MediaVerificationStatus> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default);
}
