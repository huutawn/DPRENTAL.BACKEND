namespace Dprental.MediaService.Application.IServices;

/// <summary>
/// Service điều phối dọn dẹp các object rác và phiên tải tệp hết hạn.
/// </summary>
public interface IMediaCleanupService
{
    /// <summary>
    /// Quét và xử lý các tác vụ dọn dẹp object rác trên Object Storage có retry.
    /// </summary>
    Task<int> ProcessPendingCleanupsAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quét các phiên upload đang Pending nhưng đã quá hạn (15 phút) để chuyển sang Expired và đưa object vào hàng đợi dọn dẹp.
    /// </summary>
    Task<int> SweepExpiredSessionsAsync(int limit = 50, CancellationToken cancellationToken = default);
}
