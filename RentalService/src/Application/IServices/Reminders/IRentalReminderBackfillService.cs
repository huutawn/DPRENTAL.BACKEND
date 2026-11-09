namespace Dprental.RentalService.Application.IServices.Reminders;

public sealed record BackfillProgress(
    int BatchNumber,
    int HandoverCreated,
    int ReturnCreated,
    Guid LastProcessedItemId);

public sealed record BackfillResult(
    int HandoverSchedulesCreated,
    int ReturnSchedulesCreated,
    int TotalBatches,
    Guid? LastProcessedItemId = null);

public interface IRentalReminderBackfillService
{
    Task<BackfillResult> BackfillActiveItemsAsync(
        int batchSize = 100,
        Guid? startAfterItemId = null,
        bool apply = true,
        Action<BackfillProgress>? onBatchCompleted = null,
        CancellationToken cancellationToken = default);

    Task<BackfillResult> BackfillActiveItemsAsync(
        int batchSize,
        CancellationToken cancellationToken) =>
        BackfillActiveItemsAsync(batchSize, startAfterItemId: null, apply: true, onBatchCompleted: null, cancellationToken);
}
