using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RentalReminderScheduleRepository : IRentalReminderScheduleRepository
{
    private readonly RentalServiceDbContext _dbContext;

    public RentalReminderScheduleRepository(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<ReminderClaimDescriptor>> ClaimDueBatchAsync(
        string workerId,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (batchSize <= 0) return Array.Empty<ReminderClaimDescriptor>();

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        double leaseSeconds = Math.Max(1, leaseDuration.TotalSeconds);
        Guid leaseToken = Guid.NewGuid();

        if (_dbContext.Database.IsNpgsql())
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var claimed = await _dbContext.RentalReminderSchedules
                .FromSqlInterpolated($@"
                    WITH candidate AS (
                        SELECT s.""Id""
                        FROM rental.rental_reminder_schedules s
                        WHERE s.status = 'Active'
                          AND s.remaining_unit_count > 0
                          AND (s.next_retry_at IS NULL OR s.next_retry_at <= NOW())
                          AND s.next_trigger_at IS NOT NULL
                          AND s.next_trigger_at <= NOW()
                          AND (s.leased_until IS NULL OR s.leased_until <= NOW())
                        ORDER BY s.next_trigger_at, s.""Id""
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE rental.rental_reminder_schedules u
                    SET leased_by = {workerId},
                        lease_token = {leaseToken},
                        leased_until = NOW() + ({leaseSeconds} * interval '1 second'),
                        updated_at = NOW()
                    FROM candidate
                    WHERE u.""Id"" = candidate.""Id""
                    RETURNING u.*;")
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return claimed.Select(s => new ReminderClaimDescriptor(
                s.Id,
                s.RentalOrderId,
                s.RentalItemId,
                s.ScheduleType,
                s.LeaseToken ?? leaseToken,
                s.LeasedUntil ?? utcNow.AddSeconds(leaseSeconds))).ToList();
        }
        else
        {
            var candidates = await _dbContext.RentalReminderSchedules
                .Where(s => s.Status == RentalReminderScheduleStatus.Active
                         && s.RemainingUnitCount > 0
                         && (s.NextRetryAt == null || s.NextRetryAt <= utcNow)
                         && s.NextTriggerAt != null
                         && s.NextTriggerAt <= utcNow
                         && (s.LeasedUntil == null || s.LeasedUntil <= utcNow))
                .OrderBy(s => s.NextTriggerAt)
                .ThenBy(s => s.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            DateTimeOffset leaseUntil = utcNow.Add(leaseDuration);
            foreach (var item in candidates)
            {
                item.Claim(workerId, leaseToken, leaseUntil, utcNow);
            }
            await _dbContext.SaveChangesAsync(cancellationToken);

            return candidates.Select(s => new ReminderClaimDescriptor(
                s.Id,
                s.RentalOrderId,
                s.RentalItemId,
                s.ScheduleType,
                leaseToken,
                leaseUntil)).ToList();
        }
    }

    public async Task<ReminderOrderProjection?> GetOrderReminderProjectionAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var data = await _dbContext.RentalOrders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new
            {
                o.Id,
                o.Status,
                o.OrderCode,
                o.RenterId,
                o.Version,
                Items = o.Items.Select(i => new
                {
                    i.Id,
                    i.SupplierId,
                    i.Status,
                    i.ExpectedStartDate,
                    i.ExpectedEndDate,
                    i.Quantity,
                    HandoverInProgress = o.OperationReceipts.Any(r =>
                        r.TargetItemId == i.Id &&
                        r.Action == "MarkAssetsHandedOver" &&
                        (r.Status == RentalOperationReceipt.StatusPending ||
                         r.Status == RentalOperationReceipt.StatusProcessing ||
                         r.Status == RentalOperationReceipt.StatusManualReconciliationRequired)),
                    Assignments = i.UnitAssignments.Select(a => new
                    {
                        a.Id,
                        IsReturned = a.ReturnRecordId != null
                    }),
                    ActiveRequestUnits = i.ReturnRequests
                        .SelectMany(r => r.Units)
                        .Where(u => u.ClosedAt == null && u.RejectedAt == null)
                        .Select(u => u.RentalUnitAssignmentId)
                })
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (data == null) return null;

        var items = data.Items.Select(i =>
        {
            var activeRequestedSet = i.ActiveRequestUnits.ToHashSet();
            int activeRemaining = i.Assignments.Count(a => !a.IsReturned && !activeRequestedSet.Contains(a.Id));
            return new ReminderItemProjection(
                i.Id,
                i.SupplierId,
                i.Status,
                i.ExpectedStartDate,
                i.ExpectedEndDate,
                i.Quantity,
                activeRemaining,
                i.HandoverInProgress);
        }).ToList();

        return new ReminderOrderProjection(
            data.Id,
            data.Status,
            data.OrderCode,
            data.RenterId,
            data.Version,
            items);
    }

    public async Task<IReadOnlyList<Guid>> LockSchedulesForUpdateAsync(
        IEnumerable<Guid> scheduleIds,
        CancellationToken cancellationToken = default)
    {
        var idList = scheduleIds.Distinct().OrderBy(id => id).ToList();
        if (idList.Count == 0) return Array.Empty<Guid>();

        if (_dbContext.Database.IsNpgsql())
        {
            return await _dbContext.RentalReminderSchedules
                .FromSqlInterpolated($@"
                    SELECT s.*
                    FROM rental.rental_reminder_schedules s
                    WHERE s.""Id"" = ANY({idList.ToArray()})
                    ORDER BY s.""Id""
                    FOR UPDATE;")
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }
        else
        {
            return await _dbContext.RentalReminderSchedules
                .Where(s => idList.Contains(s.Id))
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }
    }

    public async Task<bool> AdvanceScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateOnly notifiedDate,
        DateTimeOffset firedAt,
        DateTimeOffset? nextTriggerAt,
        bool isOverdue,
        bool markCompleted,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            string statusVal = markCompleted ? "Completed" : "Active";
            int rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_reminder_schedules
                SET last_notified_date = {notifiedDate},
                    last_notified_at = {firedAt},
                    next_trigger_at = {nextTriggerAt},
                    retry_count = 0,
                    next_retry_at = NULL,
                    leased_by = NULL,
                    lease_token = NULL,
                    leased_until = NULL,
                    status = CASE WHEN {markCompleted} THEN 'Completed' ELSE status END,
                    overdue_day_count = CASE WHEN {isOverdue} THEN overdue_day_count + 1 ELSE overdue_day_count END,
                    updated_at = {firedAt}
                WHERE ""Id"" = {scheduleId}
                  AND status = 'Active'
                  AND lease_token = {leaseToken}
                  AND (leased_until IS NOT NULL AND leased_until >= {firedAt});",
                cancellationToken);

            return rowsAffected > 0;
        }
        else
        {
            var schedule = await _dbContext.RentalReminderSchedules
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.LeaseToken == leaseToken, cancellationToken);
            if (schedule == null || schedule.LeasedUntil < firedAt) return false;

            schedule.AdvanceSchedule(notifiedDate, firedAt, nextTriggerAt, isOverdue, firedAt);
            if (markCompleted) schedule.Complete(firedAt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> FailScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset nextRetryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_reminder_schedules
                SET retry_count = retry_count + 1,
                    next_retry_at = {nextRetryAt},
                    leased_by = NULL,
                    lease_token = NULL,
                    leased_until = NULL,
                    updated_at = {now}
                WHERE ""Id"" = {scheduleId}
                  AND lease_token = {leaseToken};",
                cancellationToken);

            return rowsAffected > 0;
        }
        else
        {
            var schedule = await _dbContext.RentalReminderSchedules
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.LeaseToken == leaseToken, cancellationToken);
            if (schedule == null) return false;

            schedule.Fail(nextRetryAt, now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> ReleaseLeaseWithTokenAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset? postponeTriggerTo,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_reminder_schedules
                SET leased_by = NULL,
                    lease_token = NULL,
                    leased_until = NULL,
                    next_trigger_at = COALESCE({postponeTriggerTo}, next_trigger_at),
                    updated_at = {now}
                WHERE ""Id"" = {scheduleId}
                  AND lease_token = {leaseToken};",
                cancellationToken);

            return rowsAffected > 0;
        }
        else
        {
            var schedule = await _dbContext.RentalReminderSchedules
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.LeaseToken == leaseToken, cancellationToken);
            if (schedule == null) return false;

            schedule.ReleaseLease(postponeTriggerTo, now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<bool> CompleteScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext.Database.IsNpgsql())
        {
            int rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE rental.rental_reminder_schedules
                SET status = 'Completed',
                    next_trigger_at = NULL,
                    leased_by = NULL,
                    lease_token = NULL,
                    leased_until = NULL,
                    updated_at = {now}
                WHERE ""Id"" = {scheduleId}
                  AND lease_token = {leaseToken};",
                cancellationToken);

            return rowsAffected > 0;
        }
        else
        {
            var schedule = await _dbContext.RentalReminderSchedules
                .FirstOrDefaultAsync(s => s.Id == scheduleId && s.LeaseToken == leaseToken, cancellationToken);
            if (schedule == null) return false;

            schedule.Complete(now);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<RentalReminderSchedule?> GetByItemAndTypeAsync(
        Guid rentalItemId,
        RentalReminderScheduleType scheduleType,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalReminderSchedules
            .FirstOrDefaultAsync(s => s.RentalItemId == rentalItemId && s.ScheduleType == scheduleType, cancellationToken);
    }

    public async Task<IReadOnlyList<RentalReminderSchedule>> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalReminderSchedules
            .Where(s => s.RentalOrderId == orderId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RentalReminderSchedule>> GetByItemIdAsync(
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.RentalReminderSchedules
            .Where(s => s.RentalItemId == itemId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(
        RentalReminderSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await _dbContext.RentalReminderSchedules.AddAsync(schedule, cancellationToken);
    }

    public Task UpdateAsync(
        RentalReminderSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _dbContext.RentalReminderSchedules.Update(schedule);
        return Task.CompletedTask;
    }

    public Task UpdateRangeAsync(
        IEnumerable<RentalReminderSchedule> schedules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        _dbContext.RentalReminderSchedules.UpdateRange(schedules);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RentalReminderSchedule>> GetActiveSchedulesPagedAsync(
        Guid? afterId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.RentalReminderSchedules
            .AsNoTracking()
            .Where(s => s.Status == RentalReminderScheduleStatus.Active);

        if (afterId.HasValue && afterId.Value != Guid.Empty)
        {
            query = query.Where(s => s.Id > afterId.Value);
        }

        return await query
            .OrderBy(s => s.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }
}
