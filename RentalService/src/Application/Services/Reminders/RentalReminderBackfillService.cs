#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderBackfillService : IRentalReminderBackfillService
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly RentalServiceDbContext? _dbContext;
    private readonly IRentalReminderScheduleRepository? _scheduleRepo;
    private readonly IOrderLockManager? _lockManager;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReminderBackfillService> _logger;

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static readonly IAsyncDisposable NoOpLock = new NoOpAsyncDisposable();

    [ActivatorUtilitiesConstructor]
    public RentalReminderBackfillService(
        IServiceScopeFactory scopeFactory,
        IOrderLockManager orderLockManager,
        TimeProvider timeProvider,
        ILogger<RentalReminderBackfillService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _lockManager = orderLockManager ?? throw new ArgumentNullException(nameof(orderLockManager));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RentalReminderBackfillService(
        RentalServiceDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<RentalReminderBackfillService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _scheduleRepo = new RentalReminderScheduleRepository(dbContext);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lockManager = null;
    }

    public async Task<BackfillResult> BackfillActiveItemsAsync(
        int batchSize = 100,
        Guid? startAfterItemId = null,
        bool apply = true,
        Action<BackfillProgress>? onBatchCompleted = null,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) batchSize = 100;

        int totalHandoverCreated = 0;
        int totalReturnCreated = 0;
        int totalBatches = 0;
        Guid? lastProcessedItemId = startAfterItemId;

        RentalItemStatus? checkpointStatus = await GetCheckpointItemStatusAsync(startAfterItemId, cancellationToken);

        bool skipHandover = checkpointStatus == RentalItemStatus.InRental;
        Guid? handoverLastItemId = skipHandover ? null : startAfterItemId;
        Guid? returnLastItemId = skipHandover ? startAfterItemId : null;

        // 1. Handover phase for AwaitingHandover items
        if (!skipHandover)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset batchNow = _timeProvider.GetUtcNow();

                using var batchScope = _scopeFactory?.CreateScope();
                var repo = batchScope != null
                    ? batchScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>()
                    : (_scheduleRepo ?? new RentalReminderScheduleRepository(_dbContext!));
                var lockMgr = batchScope?.ServiceProvider.GetService<IOrderLockManager>() ?? _lockManager;

                var candidates = await repo.GetHandoverBackfillCandidatesAsync(handoverLastItemId, batchSize, cancellationToken);
                if (candidates.Count == 0) break;

                totalBatches++;
                handoverLastItemId = candidates[^1].ItemId;
                lastProcessedItemId = handoverLastItemId;
                int batchHandoverCreated = 0;

                var orderGroups = candidates.GroupBy(c => c.OrderId);
                foreach (var group in orderGroups)
                {
                    Guid orderId = group.Key;
                    await using var orderLock = lockMgr != null
                        ? await lockMgr.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken)
                        : NoOpLock;

                    foreach (var candidate in group)
                    {
                        var eval = RentalReminderPolicy.Evaluate(
                            RentalReminderScheduleType.Handover,
                            candidate.TargetDate,
                            lastNotifiedDate: null,
                            overdueDayCount: 0,
                            batchNow);

                        if (eval.Action == ReminderPolicyAction.WindowExpired)
                        {
                            _logger.LogInformation("Item {ItemId} Handover schedule skipped: WindowExpired", candidate.ItemId);
                            continue;
                        }

                        DateTimeOffset? triggerAt = eval.Action switch
                        {
                            ReminderPolicyAction.SendToday => batchNow,
                            ReminderPolicyAction.Reschedule => eval.NextTriggerAt,
                            _ => null
                        };

                        var schedule = RentalReminderSchedule.Create(
                            candidate.OrderId,
                            candidate.ItemId,
                            RentalReminderScheduleType.Handover,
                            candidate.Quantity,
                            triggerAt,
                            batchNow);

                        if (apply)
                        {
                            bool created = await repo.TryCreateScheduleIfMissingAsync(schedule, cancellationToken);
                            if (created) batchHandoverCreated++;
                        }
                        else
                        {
                            batchHandoverCreated++;
                        }
                    }
                }

                totalHandoverCreated += batchHandoverCreated;
                _logger.LogInformation("Backfill batch {Batch}: Handover={Count}, Checkpoint={LastId}",
                    totalBatches, batchHandoverCreated, lastProcessedItemId);

                onBatchCompleted?.Invoke(new BackfillProgress(totalBatches, batchHandoverCreated, 0, lastProcessedItemId.Value));
            }
        }

        // 2. Return phase for InRental items
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset batchNow = _timeProvider.GetUtcNow();

            using var batchScope = _scopeFactory?.CreateScope();
            var repo = batchScope != null
                ? batchScope.ServiceProvider.GetRequiredService<IRentalReminderScheduleRepository>()
                : (_scheduleRepo ?? new RentalReminderScheduleRepository(_dbContext!));
            var lockMgr = batchScope?.ServiceProvider.GetService<IOrderLockManager>() ?? _lockManager;

            var candidates = await repo.GetReturnBackfillCandidatesAsync(returnLastItemId, batchSize, cancellationToken);
            if (candidates.Count == 0) break;

            totalBatches++;
            returnLastItemId = candidates[^1].ItemId;
            lastProcessedItemId = returnLastItemId;
            int batchReturnCreated = 0;

            var orderGroups = candidates.GroupBy(c => c.OrderId);
            foreach (var group in orderGroups)
            {
                Guid orderId = group.Key;
                await using var orderLock = lockMgr != null
                    ? await lockMgr.AcquireOrderLockAsync(orderId, TimeSpan.FromSeconds(15), cancellationToken)
                    : NoOpLock;

                foreach (var candidate in group)
                {
                    var eval = RentalReminderPolicy.Evaluate(
                        RentalReminderScheduleType.Return,
                        candidate.TargetDate,
                        lastNotifiedDate: null,
                        overdueDayCount: 0,
                        batchNow);

                    if (eval.Action == ReminderPolicyAction.WindowExpired)
                    {
                        _logger.LogInformation("Item {ItemId} Return schedule skipped: WindowExpired", candidate.ItemId);
                        continue;
                    }

                    DateTimeOffset? triggerAt = candidate.Quantity > 0
                        ? (eval.Action switch
                        {
                            ReminderPolicyAction.SendToday => batchNow,
                            ReminderPolicyAction.Reschedule => eval.NextTriggerAt,
                            _ => null
                        })
                        : null;

                    var schedule = RentalReminderSchedule.Create(
                        candidate.OrderId,
                        candidate.ItemId,
                        RentalReminderScheduleType.Return,
                        candidate.Quantity,
                        triggerAt,
                        batchNow);

                    if (apply)
                    {
                        bool created = await repo.TryCreateScheduleIfMissingAsync(schedule, cancellationToken);
                        if (created) batchReturnCreated++;
                    }
                    else
                    {
                        batchReturnCreated++;
                    }
                }
            }

            totalReturnCreated += batchReturnCreated;
            _logger.LogInformation("Backfill batch {Batch}: Return={Count}, Checkpoint={LastId}",
                totalBatches, batchReturnCreated, lastProcessedItemId);

            onBatchCompleted?.Invoke(new BackfillProgress(totalBatches, 0, batchReturnCreated, lastProcessedItemId.Value));
        }

        return new BackfillResult(totalHandoverCreated, totalReturnCreated, totalBatches, lastProcessedItemId);
    }

    private async Task<RentalItemStatus?> GetCheckpointItemStatusAsync(Guid? checkpointId, CancellationToken cancellationToken)
    {
        if (!checkpointId.HasValue || checkpointId.Value == Guid.Empty)
        {
            return null;
        }

        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            return await db.RentalItems
                .AsNoTracking()
                .Where(i => i.Id == checkpointId.Value)
                .Select(i => (RentalItemStatus?)i.Status)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else if (_dbContext != null)
        {
            return await _dbContext.RentalItems
                .AsNoTracking()
                .Where(i => i.Id == checkpointId.Value)
                .Select(i => (RentalItemStatus?)i.Status)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return null;
    }
}
