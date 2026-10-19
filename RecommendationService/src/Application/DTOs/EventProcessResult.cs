namespace Dprental.RecommendationService.Application.DTOs;

public enum EventProcessOutcome
{
    Processed,
    DuplicateIgnored,
    ConflictMismatch,
    SchemaValidationError,
    SkippedUnrelated
}

public sealed record EventProcessResult(EventProcessOutcome Outcome, string? ErrorReason = null);
