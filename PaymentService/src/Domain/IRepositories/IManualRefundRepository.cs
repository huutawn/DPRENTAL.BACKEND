using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.IRepositories;

public sealed record ManualRefundCompletion(
    Guid RequirementId, string RequirementRef, Guid RentalItemId,
    string SettlementRef, decimal Amount, string ExternalTransferReference,
    DateTimeOffset CompletedAt, Guid CompletedBy, bool IsReplay);

public interface IManualRefundRepository
{
    Task<Result<ManualRefundCompletion>> CompleteAsync(
        Guid requirementId,
        string externalTransferReference,
        Guid adminId,
        CancellationToken cancellationToken = default);

    Task<ManualRefundCompletion?> GetCompletedReplayAsync(
        Guid requirementId,
        string externalTransferReference,
        CancellationToken cancellationToken = default);
}
