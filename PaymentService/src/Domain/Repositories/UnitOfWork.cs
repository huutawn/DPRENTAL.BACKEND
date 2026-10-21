using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly PaymentServiceDbContext _dbContext;

    public UnitOfWork(PaymentServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        return new EfExecutionTransaction(tx);
    }

    public async Task ExecuteStreamLockAsync(string streamId, CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({streamId}))", cancellationToken);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed class EfExecutionTransaction : IExecutionTransaction
    {
        private readonly IDbContextTransaction _tx;

        public EfExecutionTransaction(IDbContextTransaction tx) => _tx = tx;

        public Task CommitAsync(CancellationToken cancellationToken = default) => _tx.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) => _tx.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => _tx.DisposeAsync();
    }
}
