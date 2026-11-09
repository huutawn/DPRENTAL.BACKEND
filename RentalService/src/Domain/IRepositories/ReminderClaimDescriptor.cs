using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

public sealed record ReminderClaimDescriptor(
    Guid ScheduleId,
    Guid OrderId,
    Guid ItemId,
    RentalReminderScheduleType ScheduleType,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiry);
