namespace Dprental.RentalService.Domain.IRepositories;

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract Unit of Work để quản lý giao dịch và lưu thay đổi aggregate.
/// </summary>
public interface IUnitOfWork
{
    bool HasActiveTransaction => false;
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<int> CommitAsync(CancellationToken cancellationToken = default) => SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Bỏ mọi thay đổi chưa lưu đang được theo dõi. Gọi sau khi lưu thất bại để lần lưu sau
    /// trong cùng request không ghi lại dữ liệu hỏng.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// Bắt đầu một transaction mới trên Unit of Work.
    /// </summary>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
