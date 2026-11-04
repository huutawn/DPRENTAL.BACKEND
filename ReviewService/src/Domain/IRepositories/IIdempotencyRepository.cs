using Dprental.ReviewService.Common;
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
    /// Lấy khóa PostgreSQL transaction advisory lock theo cặp ActorId và IdempotencyKey. Bắt buộc có transaction đang mở.
    /// </summary>
    Task AcquireLockAsync(Guid actorId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lưu hoặc cập nhật bản ghi chống trùng lặp an toàn đa luồng bằng PostgreSQL advisory lock.
    /// </summary>
    Task<Result<IdempotencyRecord>> SaveAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);
}
