using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.IRepositories;

public sealed record CompleteSupplierPayoutCommand(
    Guid PayoutId, Guid AdminId, string OperationKey, string ExternalTransferReference, string AuditReason);

public sealed record SupplierPayoutWriteResult(
    Guid PayoutId, string PayoutRef, string Status,
    string? ExternalTransferReference, DateTimeOffset? CompletedAt, Guid? CompletedBy, bool IsReplay);

public sealed record SupplierPayoutPublication(
    Dprental.PaymentService.Domain.Entities.OutboxMessage Message,
    Dprental.PaymentService.Domain.Entities.TransactionJournalEntry Journal);

public interface ISupplierPayoutRepository
{
    Task<OutgoingTransferTarget?> FindByTransferCodeAsync(string transferCode, CancellationToken ct);
    Task<Result<SupplierPayoutWriteResult>> CompleteAsync(CompleteSupplierPayoutCommand command,
        Func<Dprental.PaymentService.Domain.Entities.SupplierPayout, DateTimeOffset, CancellationToken,
            Task<SupplierPayoutPublication>> createPublication, CancellationToken ct);
}
