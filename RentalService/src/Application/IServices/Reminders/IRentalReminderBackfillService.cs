namespace Dprental.RentalService.Application.IServices.Reminders;

public sealed record BackfillResult(int HandoverSchedulesCreated, int ReturnSchedulesCreated, int TotalBatches);

public interface IRentalReminderBackfillService
{
    Task<BackfillResult> BackfillActiveItemsAsync(int batchSize = 100, CancellationToken cancellationToken = default);
}
