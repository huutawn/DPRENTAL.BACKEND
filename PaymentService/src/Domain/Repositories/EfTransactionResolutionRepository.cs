using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class EfTransactionResolutionRepository(PaymentServiceDbContext db)
    : ITransactionResolutionRepository
{
    public async Task<PaymentTransaction?> FindTransactionWithRowLockAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        var transactions = await db.PaymentTransactions
            .FromSqlInterpolated($"SELECT * FROM payment_transactions WHERE \"Id\" = {transactionId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return transactions.SingleOrDefault();
    }

    public async Task<PaymentTransaction?> FindTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        return await db.PaymentTransactions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken);
    }

    public async Task<PaymentTransactionResolution?> FindResolutionByOperationKeyAsync(string operationKey, CancellationToken cancellationToken = default)
    {
        return await db.PaymentTransactionResolutions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationKey == operationKey, cancellationToken);
    }

    public async Task<bool> ExistsResolutionByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        return await db.PaymentTransactionResolutions.AsNoTracking()
            .AnyAsync(x => x.TransactionId == transactionId, cancellationToken);
    }

    public async Task SaveResolutionAsync(
        PaymentTransactionResolution resolution,
        TransactionJournalEntry journal,
        OutboxMessage outbox,
        CancellationToken cancellationToken = default)
    {
        db.PaymentTransactionResolutions.Add(resolution);
        db.TransactionJournals.Add(journal);
        db.OutboxMessages.Add(outbox);
        await db.SaveChangesAsync(cancellationToken);
    }
}
