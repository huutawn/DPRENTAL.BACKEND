using Dprental.MediaService.Domain.Entities;

namespace Dprental.MediaService.Domain.IRepositories;

/// <summary>
/// Hợp đồng truy xuất và lưu trữ phiên tải lên MediaUploadSession.
/// </summary>
public interface IMediaUploadSessionRepository
{
    Task<MediaUploadSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MediaUploadSession?> FindByCallerAndIdempotencyKeyAsync(string callerIdentity, string idempotencyKey, CancellationToken cancellationToken = default);
    Task AddAsync(MediaUploadSession session, CancellationToken cancellationToken = default);
    Task UpdateAsync(MediaUploadSession session, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaUploadSession>> GetExpiredPendingSessionsAsync(DateTimeOffset cutoffUtc, int limit, CancellationToken cancellationToken = default);
}
