using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepositories;

public interface ITransactionResolutionRepository
{
    Task<PaymentTransaction?> FindTransactionWithRowLockAsync(Guid transactionId, CancellationToken cancellationToken = default);
    Task<PaymentTransaction?> FindTransactionByIdAsync(Guid transactionId, CancellationToken cancellationToken = default);
    Task<PaymentTransactionResolution?> FindResolutionByOperationKeyAsync(string operationKey, CancellationToken cancellationToken = default);
    Task<bool> ExistsResolutionByTransactionIdAsync(Guid transactionId, CancellationToken cancellationToken = default);
    Task SaveResolutionAsync(PaymentTransactionResolution resolution, TransactionJournalEntry journal, OutboxMessage outbox, CancellationToken cancellationToken = default);
}
