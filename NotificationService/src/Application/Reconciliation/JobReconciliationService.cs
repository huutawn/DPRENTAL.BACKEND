using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.NotificationService.Application.Reconciliation;

public sealed class JobReconciliationService : IJobReconciliationService
{
    private static readonly string[] EligibleNotificationTypes =
    [
        "ORDER_PAID",
        "ORDER_COMPLETED",
        "REFUND_COMPLETED",
        "PAYOUT_COMPLETED"
    ];

    private static readonly string[] EligibleErrorCodes =
    [
        "STALE_ACTION_SUPPRESSED",
        "STALE_EVENT_TERMINATED"
    ];

    private readonly IDeliveryJobRepository _deliveryJobRepo;
    private readonly ILogger<JobReconciliationService> _logger;

    public JobReconciliationService(
        IDeliveryJobRepository deliveryJobRepo,
        ILogger<JobReconciliationService>? logger = null)
    {
        _deliveryJobRepo = deliveryJobRepo ?? throw new ArgumentNullException(nameof(deliveryJobRepo));
        _logger = logger ?? NullLogger<JobReconciliationService>.Instance;
    }

    public async Task<ReconciliationCandidateReport> GetCandidatesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Min(Math.Max(1, limit), 100);
        var candidateIds = await _deliveryJobRepo.GetReconciliationCandidateJobIdsAsync(
            EligibleNotificationTypes,
            EligibleErrorCodes,
            boundedLimit,
            cancellationToken);

        _logger.LogInformation("JobReconciliationService: identified {Count} candidate jobs for reconciliation (limit={Limit}).",
            candidateIds.Count, boundedLimit);

        return new ReconciliationCandidateReport(candidateIds.Count, candidateIds);
    }

    public async Task<ReconciliationExecutionReport> RequeueJobsAsync(
        IReadOnlyList<Guid> jobIds,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        if (jobIds == null || jobIds.Count == 0)
        {
            return new ReconciliationExecutionReport(operatorId ?? "unknown", 0, 0, Array.Empty<Guid>(), Array.Empty<Guid>(), "No job IDs provided.");
        }

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new ArgumentException("Operator ID or run ID is mandatory for job reconciliation execution.", nameof(operatorId));
        }

        var distinctJobIds = jobIds.Distinct().ToList();
        if (distinctJobIds.Count > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(jobIds), "Cannot reconcile more than 100 distinct jobs in a single request.");
        }

        var reconciledIds = await _deliveryJobRepo.RequeueReconciliationJobsAsync(
            distinctJobIds,
            EligibleNotificationTypes,
            EligibleErrorCodes,
            operatorId,
            cancellationToken);

        var reconciledSet = new HashSet<Guid>(reconciledIds);
        var skippedIds = distinctJobIds.Where(id => !reconciledSet.Contains(id)).ToList();

        _logger.LogInformation("JobReconciliationService: operator '{OperatorId}' requeued {Reconciled}/{Requested} jobs (skipped {Skipped}).",
            operatorId, reconciledIds.Count, distinctJobIds.Count, skippedIds.Count);

        return new ReconciliationExecutionReport(
            OperatorId: operatorId,
            RequestedCount: distinctJobIds.Count,
            ReconciledCount: reconciledIds.Count,
            ReconciledJobIds: reconciledIds,
            SkippedJobIds: skippedIds,
            Message: $"Successfully requeued {reconciledIds.Count} jobs to Pending ({skippedIds.Count} skipped).");
    }
}
