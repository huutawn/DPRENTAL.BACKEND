using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.IServices.Reminders;

public interface IRentalReminderScheduleService
{
    Task EnsureHandoverScheduleCreatedAsync(
        Guid orderId,
        Guid itemId,
        DateOnly expectedStartDate,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task CompleteHandoverAndCreateReturnScheduleAsync(
        Guid orderId,
        Guid itemId,
        DateOnly expectedEndDate,
        int quantity,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task OnReturnUnitsChangedAsync(
        Guid itemId,
        int remainingActiveUnits,
        DateOnly expectedEndDate,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task StopSchedulesForItemAsync(
        Guid itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
