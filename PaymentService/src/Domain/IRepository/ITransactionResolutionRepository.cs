using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepository;

public sealed record ResolveTransactionCommand(
    Guid TransactionId, Guid AdminId, string InvoiceRef, string OperationKey,
    string ExternalReference, string AuditReason, string PayloadFingerprint);

public sealed record TransactionResolutionResult(
    Guid ResolutionId, Guid TransactionId, string InvoiceRef, string PaymentRef,
    DateTimeOffset ResolvedAt, Guid ResolvedBy, bool IsReplay);

public interface ITransactionResolutionRepository
{
    Task<Result<TransactionResolutionResult>> ResolveAsync(
        ResolveTransactionCommand command,
        Func<PaymentTransaction, Invoice, DateTimeOffset, Result<OutboxMessage>> validateAndCreateEvent,
        CancellationToken cancellationToken);
}
