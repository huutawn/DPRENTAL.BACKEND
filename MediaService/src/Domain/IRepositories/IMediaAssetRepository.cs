using Dprental.MediaService.Domain.Entities;

namespace Dprental.MediaService.Domain.IRepositories;

/// <summary>
/// Hợp đồng truy xuất và lưu trữ MediaAsset.
/// </summary>
public interface IMediaAssetRepository
{
    Task<MediaAsset?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MediaAsset?> GetByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(MediaAsset asset, CancellationToken cancellationToken = default);
    Task UpdateAsync(MediaAsset asset, CancellationToken cancellationToken = default);
}
