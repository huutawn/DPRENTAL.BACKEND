using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IDeliveryJobRepository
{
    Task AddAsync(NotificationDeliveryJob job, CancellationToken cancellationToken = default);
    Task AddRangeAsync(IEnumerable<NotificationDeliveryJob> jobs, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDeliveryJob>> ClaimPendingJobsAsync(string workerId, TimeSpan leaseDuration, int batchSize, CancellationToken cancellationToken = default);
    Task<int> RecoverExpiredLeasesAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateJobStatusAsync(
        Guid jobId,
        string leaseToken,
        string newStatus,
        string? providerMessageId = null,
        string? errorCode = null,
        string? errorDetail = null,
        DateTimeOffset? nextAttemptAt = null,
        bool resetDispatchStartedAt = false,
        CancellationToken cancellationToken = default);
    Task<bool> RecordDispatchStartAsync(Guid jobId, string leaseToken, DateTimeOffset dispatchStartedAt, CancellationToken cancellationToken = default);
    Task<int> SuppressPendingAndUndispatchedJobsForResourceAsync(string resourceType, string resourceId, string reason, CancellationToken cancellationToken = default);
    Task<int> SuppressPendingAndUndispatchedJobsForOrderAsync(Guid orderId, string reason, CancellationToken cancellationToken = default);
    Task<int> SuppressPendingAndUndispatchedJobsForRentalItemAsync(Guid rentalItemId, string reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetReconciliationCandidateJobIdsAsync(
        IReadOnlyCollection<string> eligibleNotificationTypes,
        IReadOnlyCollection<string> eligibleErrorCodes,
        int limit = 100,
        CancellationToken cancellationToken = default);
    Task<int> RequeueReconciliationJobsAsync(
        IReadOnlyList<Guid> jobIds,
        IReadOnlyCollection<string> eligibleNotificationTypes,
        IReadOnlyCollection<string> eligibleErrorCodes,
        string operatorId,
        CancellationToken cancellationToken = default);
}
