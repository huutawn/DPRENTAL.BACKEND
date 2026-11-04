using Dprental.NotificationService.Domain.IRepositories;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dprental.NotificationService.Application.Reconciliation;

public sealed class JobReconciliationService : IJobReconciliationService
{
    private static readonly HashSet<string> ResultNotificationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORDER_PAID",
        "ORDER_COMPLETED",
        "REFUND_COMPLETED",
        "PAYOUT_COMPLETED"
    };

    private readonly NotificationServiceDbContext _dbContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<JobReconciliationService> _logger;

    public JobReconciliationService(
        NotificationServiceDbContext dbContext,
        IUnitOfWork unitOfWork,
        ILogger<JobReconciliationService>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? NullLogger<JobReconciliationService>.Instance;
    }

    public async Task<ReconciliationReport> ReconcileSuppressedJobsAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var candidateJobsQuery = _dbContext.DeliveryJobs
            .Include(j => j.Notification)
            .Where(j => j.Status == "Suppressed"
                     && j.LastErrorCode == "STALE_ACTION_SUPPRESSED"
                     && j.DispatchStartedAt == null
                     && j.Notification != null
                     && ResultNotificationTypes.Contains(j.Notification.NotificationType));

        if (dryRun)
        {
            var candidateJobIds = await candidateJobsQuery
                .Select(j => j.Id)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("JobReconciliationService dry-run: tìm thấy {Count} candidate jobs.", candidateJobIds.Count);

            return new ReconciliationReport(
                DryRun: true,
                CandidateCount: candidateJobIds.Count,
                ReconciledCount: 0,
                JobIds: candidateJobIds,
                Message: $"Dry run: {candidateJobIds.Count} candidate jobs identified for reconciliation.");
        }

        await using var tx = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var jobsToReconcile = await candidateJobsQuery.ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var reconciledIds = new List<Guid>(jobsToReconcile.Count);

        foreach (var job in jobsToReconcile)
        {
            job.Status = "Pending";
            job.LastErrorCode = null;
            job.LastErrorDetail = $"Reconciled from STALE_ACTION_SUPPRESSED at {now:O} (audit: false suppression fix)";
            job.NextAttemptAt = now;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            job.UpdatedAt = now;
            reconciledIds.Add(job.Id);
        }

        if (reconciledIds.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("JobReconciliationService đã khôi phục {Count} jobs về trạng thái Pending.", reconciledIds.Count);

        return new ReconciliationReport(
            DryRun: false,
            CandidateCount: reconciledIds.Count,
            ReconciledCount: reconciledIds.Count,
            JobIds: reconciledIds,
            Message: $"Successfully reconciled {reconciledIds.Count} jobs to Pending.");
    }
}
