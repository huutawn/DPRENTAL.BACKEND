namespace Dprental.NotificationService.Application.Reconciliation;

public sealed record ReconciliationCandidateReport(
    int TotalCandidates,
    IReadOnlyList<Guid> CandidateJobIds);

public sealed record ReconciliationExecutionReport(
    string OperatorId,
    int RequestedCount,
    int ReconciledCount,
    IReadOnlyList<Guid> ReconciledJobIds,
    string? Message = null);

public interface IJobReconciliationService
{
    Task<ReconciliationCandidateReport> GetCandidatesAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<ReconciliationExecutionReport> RequeueJobsAsync(IReadOnlyList<Guid> jobIds, string operatorId, CancellationToken cancellationToken = default);
}
