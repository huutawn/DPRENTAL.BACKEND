using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

public interface IRentalReminderScheduleRepository
{
    Task<IReadOnlyList<ReminderClaimDescriptor>> ClaimDueBatchAsync(
        string workerId,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<ReminderOrderProjection?> GetOrderReminderProjectionAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> LockSchedulesForUpdateAsync(
        IEnumerable<Guid> scheduleIds,
        CancellationToken cancellationToken = default);

    Task<bool> AdvanceScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateOnly notifiedDate,
        DateTimeOffset firedAt,
        DateTimeOffset? nextTriggerAt,
        bool isOverdue,
        bool markCompleted,
        CancellationToken cancellationToken = default);

    Task<bool> FailScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset nextRetryAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseLeaseWithTokenAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset? postponeTriggerTo,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteScheduleWithLeaseCheckAsync(
        Guid scheduleId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RentalReminderSchedule?> GetByItemAndTypeAsync(
        Guid rentalItemId,
        RentalReminderScheduleType scheduleType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalReminderSchedule>> GetByOrderIdAsync(
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalReminderSchedule>> GetByItemIdAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        RentalReminderSchedule schedule,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        RentalReminderSchedule schedule,
        CancellationToken cancellationToken = default);

    Task UpdateRangeAsync(
        IEnumerable<RentalReminderSchedule> schedules,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalReminderSchedule>> GetActiveSchedulesPagedAsync(
        Guid? afterId,
        int pageSize,
        CancellationToken cancellationToken = default);
}
