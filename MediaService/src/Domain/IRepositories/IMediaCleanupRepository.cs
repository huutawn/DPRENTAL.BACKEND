using Dprental.MediaService.Domain.Entities;

namespace Dprental.MediaService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy vấn hàng đợi dọn dẹp object rác trên Object Storage.
/// </summary>
public interface IMediaCleanupRepository
{
    Task AddAsync(MediaCleanupItem item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaCleanupItem>> GetPendingItemsAsync(int limit, CancellationToken cancellationToken = default);
    Task UpdateAsync(MediaCleanupItem item, CancellationToken cancellationToken = default);
}
