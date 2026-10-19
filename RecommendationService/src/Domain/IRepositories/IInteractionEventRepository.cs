using Dprental.RecommendationService.Domain.Entities;

namespace Dprental.RecommendationService.Domain.IRepositories;

public enum InteractionIngestStatus
{
    Inserted,
    DuplicateIgnored,
    ConflictMismatch
}

public sealed record InteractionIngestResult(InteractionIngestStatus Status, string? ConflictReason = null);

public interface IInteractionEventRepository
{
    Task<InteractionIngestResult> IngestAsync(InteractionEvent interactionEvent, CancellationToken cancellationToken);
}
