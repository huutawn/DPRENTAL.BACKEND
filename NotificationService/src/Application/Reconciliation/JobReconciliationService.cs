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
            return new ReconciliationExecutionReport(operatorId ?? "unknown", 0, 0, Array.Empty<Guid>(), "No job IDs provided.");
        }

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new ArgumentException("Operator ID or run ID is mandatory for job reconciliation execution.", nameof(operatorId));
        }

        var boundedJobIds = jobIds.Take(100).Distinct().ToList();

        var affected = await _deliveryJobRepo.RequeueReconciliationJobsAsync(
            boundedJobIds,
            EligibleNotificationTypes,
            EligibleErrorCodes,
            operatorId,
            cancellationToken);

        _logger.LogInformation("JobReconciliationService: operator '{OperatorId}' requeued {Affected}/{Requested} jobs.",
            operatorId, affected, boundedJobIds.Count);

        return new ReconciliationExecutionReport(
            OperatorId: operatorId,
            RequestedCount: boundedJobIds.Count,
            ReconciledCount: affected,
            ReconciledJobIds: boundedJobIds,
            Message: $"Successfully requeued {affected} jobs to Pending.");
    }
}
