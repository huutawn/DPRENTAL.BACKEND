using Dprental.PaymentService.Common;

namespace Dprental.PaymentService.Domain.IRepository;

public sealed record ManualRefundCompletion(
    Guid RequirementId, string RequirementRef, Guid RentalItemId,
    string SettlementRef, decimal Amount, string ExternalTransferReference,
    DateTimeOffset CompletedAt, Guid CompletedBy, bool IsReplay);

public interface IManualRefundRepository
{
    Task<Result<ManualRefundCompletion>> CompleteAsync(
        Guid requirementId, string externalTransferReference, Guid adminId,
        CancellationToken cancellationToken);
}
