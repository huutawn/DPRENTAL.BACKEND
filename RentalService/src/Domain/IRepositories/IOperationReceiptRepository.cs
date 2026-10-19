using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record OperationReceiptDescriptor(
    Guid Id,
    Guid RentalOrderId,
    string OperationId,
    string Action,
    Guid? TargetItemId,
    string Status,
    int RetryCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset CreatedAt,
    string? PrerequisiteOperationId,
    bool IsOutcomeUncertain);

public interface IOperationReceiptRepository
{
    Task<RentalOperationReceipt?> GetByOperationIdAsync(Guid orderId, string operationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationReceiptDescriptor>> GetPendingOperationDescriptorsAsync(
        DateTimeOffset utcNow,
        int batchSize,
        IReadOnlyCollection<string>? excludedActions = null,
        IReadOnlyCollection<string>? unlimitedAttemptActions = null,
        CancellationToken cancellationToken = default);

    Task AddAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default);

    Task UpdateAsync(RentalOperationReceipt receipt, CancellationToken cancellationToken = default);
}
