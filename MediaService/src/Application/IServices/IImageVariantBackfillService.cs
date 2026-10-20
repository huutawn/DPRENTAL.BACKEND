namespace Dprental.MediaService.Application.IServices;

public sealed record BackfillOptions(
    bool DryRun = false,
    int BatchSize = 25,
    Guid? AfterId = null,
    Guid? MediaId = null);

public sealed record BackfillSummary(
    int TotalProcessed,
    int SucceededCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<Guid> FailedMediaIds,
    Guid? LastCheckpointId);

public interface IImageVariantBackfillService
{
    Task<BackfillSummary> RunBackfillAsync(
        BackfillOptions options,
        CancellationToken cancellationToken = default);
}
