using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record ReminderBackfillCandidate(
    Guid ItemId,
    Guid OrderId,
    RentalReminderScheduleType ScheduleType,
    DateOnly TargetDate,
    int Quantity);
