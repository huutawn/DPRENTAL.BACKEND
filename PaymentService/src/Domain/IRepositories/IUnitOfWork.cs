

namespace Dprental.PaymentService.Domain.IRepositories;

public interface IUnitOfWork
{
    Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task ExecuteStreamLockAsync(string streamId, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
