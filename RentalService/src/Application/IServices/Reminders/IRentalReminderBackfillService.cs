namespace Dprental.RentalService.Application.IServices.Reminders;

public sealed record BackfillProgress(
    string Phase,
    int BatchNumber,
    int HandoverCreated,
    int ReturnCreated,
    Guid LastProcessedItemId,
    string ResumeCommand);

public sealed record BackfillResult(
    int HandoverSchedulesCreated,
    int ReturnSchedulesCreated,
    int TotalBatches,
    string? LastPhase = null,
    Guid? LastProcessedItemId = null,
    bool Succeeded = true,
    string? ErrorMessage = null);

public interface IRentalReminderBackfillService
{
    Task<BackfillResult> BackfillActiveItemsAsync(
        int batchSize = 100,
        string? phase = null,
        Guid? afterItemId = null,
        bool apply = true,
        Action<BackfillProgress>? onBatchCompleted = null,
        CancellationToken cancellationToken = default);
}
