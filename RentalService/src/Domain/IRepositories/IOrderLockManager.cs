namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Quản lý phân tán khóa theo phiên cho RentalOrder (PostgreSQL Session Advisory Lock).
/// </summary>
public interface IOrderLockManager
{
    /// <summary>
    /// Thu thập distributed advisory lock cho RentalOrder với thời gian chờ hữu hạn.
    /// Trả về IAsyncDisposable để giải phóng khóa khi hoàn tất.
    /// </summary>
    Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
