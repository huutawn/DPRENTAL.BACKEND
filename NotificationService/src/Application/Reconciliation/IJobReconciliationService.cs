namespace Dprental.NotificationService.Application.Reconciliation;

public sealed record ReconciliationReport(
    bool DryRun,
    int CandidateCount,
    int ReconciledCount,
    IReadOnlyList<Guid> JobIds,
    string? Message = null);

public interface IJobReconciliationService
{
    Task<ReconciliationReport> ReconcileSuppressedJobsAsync(bool dryRun, CancellationToken cancellationToken = default);
}
