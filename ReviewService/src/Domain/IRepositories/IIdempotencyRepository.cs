using Dprental.ReviewService.Domain.Entities;

namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Hợp đồng lưu trữ và truy vấn bản ghi chống trùng lặp IdempotencyRecord.
/// </summary>
public interface IIdempotencyRepository
{
    /// <summary>
    /// Lấy bản ghi chống trùng lặp theo ActorId và IdempotencyKey.
    /// </summary>
    Task<IdempotencyRecord?> GetAsync(Guid actorId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu bản ghi chống trùng lặp bất biến vào cơ sở dữ liệu.
    /// </summary>
    Task SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);
}
