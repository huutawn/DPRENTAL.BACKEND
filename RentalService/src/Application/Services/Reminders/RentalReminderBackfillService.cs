#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Reminders;

public sealed class RentalReminderBackfillService : IRentalReminderBackfillService
{
    private readonly RentalServiceDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentalReminderBackfillService> _logger;

    public RentalReminderBackfillService(
        RentalServiceDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<RentalReminderBackfillService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BackfillResult> BackfillActiveItemsAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        int handoverCreated = 0;
        int returnCreated = 0;
        int totalBatches = 0;
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // 1. Backfill Handover schedules for AwaitingHandover items
        Guid? lastItemId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var query = _dbContext.RentalItems
                .Where(i => i.Status == RentalItemStatus.AwaitingHandover
                         && !_dbContext.RentalReminderSchedules.Any(s => s.RentalItemId == i.Id && s.ScheduleType == RentalReminderScheduleType.Handover));

            if (lastItemId.HasValue)
            {
                query = query.Where(i => i.Id > lastItemId.Value);
            }

            var batch = await query
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .Select(i => new { i.Id, i.RentalOrderId, i.ExpectedStartDate, i.Quantity })
                .ToListAsync(cancellationToken);

            if (batch.Count == 0) break;

            totalBatches++;
            lastItemId = batch[^1].Id;

            foreach (var item in batch)
            {
                var triggerAt = RentalReminderPolicy.CalculateInitialTriggerAt(RentalReminderScheduleType.Handover, item.ExpectedStartDate, now);
                var schedule = RentalReminderSchedule.Create(
                    item.RentalOrderId,
                    item.Id,
                    RentalReminderScheduleType.Handover,
                    item.Quantity,
                    triggerAt,
                    now);
                await _dbContext.RentalReminderSchedules.AddAsync(schedule, cancellationToken);
                handoverCreated++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Backfilled batch {Batch}: {Count} Handover schedules created", totalBatches, batch.Count);
        }

        // 2. Backfill Return schedules for InRental items
        lastItemId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var query = _dbContext.RentalItems
                .Where(i => i.Status == RentalItemStatus.InRental
                         && !_dbContext.RentalReminderSchedules.Any(s => s.RentalItemId == i.Id && s.ScheduleType == RentalReminderScheduleType.Return));

            if (lastItemId.HasValue)
            {
                query = query.Where(i => i.Id > lastItemId.Value);
            }

            var batch = await query
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .Select(i => new
                {
                    i.Id,
                    i.RentalOrderId,
                    i.ExpectedEndDate,
                    i.Quantity,
                    ReturnedCount = i.UnitAssignments.Count(u => u.ReturnRecordId != null),
                    PendingUnits = i.ReturnRequests.SelectMany(r => r.Units).Count(u => u.ClosedAt == null && u.RejectedAt == null)
                })
                .ToListAsync(cancellationToken);

            if (batch.Count == 0) break;

            totalBatches++;
            lastItemId = batch[^1].Id;

            foreach (var item in batch)
            {
                int remaining = Math.Max(0, item.Quantity - (item.ReturnedCount + item.PendingUnits));
                DateTimeOffset? triggerAt = remaining > 0
                    ? RentalReminderPolicy.CalculateInitialTriggerAt(RentalReminderScheduleType.Return, item.ExpectedEndDate, now)
                    : null;

                var schedule = RentalReminderSchedule.Create(
                    item.RentalOrderId,
                    item.Id,
                    RentalReminderScheduleType.Return,
                    remaining,
                    triggerAt,
                    now);

                await _dbContext.RentalReminderSchedules.AddAsync(schedule, cancellationToken);
                returnCreated++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Backfilled batch {Batch}: {Count} Return schedules created", totalBatches, batch.Count);
        }

        return new BackfillResult(handoverCreated, returnCreated, totalBatches);
    }
}
