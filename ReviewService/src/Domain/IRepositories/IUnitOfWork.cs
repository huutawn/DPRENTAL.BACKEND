namespace Dprental.ReviewService.Domain.IRepositories;

/// <summary>
/// Quản lý phạm vi giao dịch Unit of Work cho các tác vụ thay đổi trạng thái đánh giá.
/// </summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract Unit of Work điều phối lưu trữ và đảm bảo tính nguyên tử giữa các repository mà không để lộ DbContext.
/// </summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<int> CommitAsync(CancellationToken cancellationToken = default);
    void DiscardChanges();
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
