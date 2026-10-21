using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.IRepositories;

public sealed record CompleteSupplierPayoutCommand(
    Guid PayoutId, Guid AdminId, string OperationKey, string ExternalTransferReference, string AuditReason);

public sealed record AttachPayoutDestinationCommand(
    Guid PayoutId, Guid AdminId, string OperationKey, string DestinationRef, string AuditReason, int Version);

public sealed record SupplierPayoutWriteResult(
    Guid PayoutId, string PayoutRef, string Status, string? DestinationRef,
    string? ExternalTransferReference, DateTimeOffset? CompletedAt, Guid? CompletedBy, bool IsReplay);

public interface ISupplierPayoutRepository
{
    Task<Result<SupplierPayoutWriteResult>> AttachDestinationAsync(AttachPayoutDestinationCommand command, CancellationToken ct);
    Task<Result<SupplierPayoutWriteResult>> CompleteAsync(CompleteSupplierPayoutCommand command, CancellationToken ct);
}
