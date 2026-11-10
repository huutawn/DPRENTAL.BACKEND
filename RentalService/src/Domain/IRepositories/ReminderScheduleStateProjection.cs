using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record ReminderScheduleStateProjection(
    Guid Id,
    RentalReminderScheduleStatus Status,
    DateOnly? LastNotifiedDate,
    int OverdueDayCount,
    int RetryCount,
    int RemainingUnitCount,
    Guid? LeaseToken,
    DateTimeOffset? LeasedUntil);
