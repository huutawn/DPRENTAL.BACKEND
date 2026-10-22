using Dprental.RecommendationService.Domain.Entities;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class InteractionEventRepository : IInteractionEventRepository
{
    private readonly RecommendationServiceDbContext _dbContext;

    public InteractionEventRepository(RecommendationServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<InteractionIngestResult> IngestAsync(InteractionEvent interactionEvent, CancellationToken cancellationToken)
    {
        var rowsAffected = await _dbContext.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO recommendation.interaction_events (
                message_id,
                message_type,
                version,
                producer,
                occurred_at,
                received_at,
                correlation_id,
                user_id,
                equipment_listing_id,
                equipment_type_id,
                score_weight,
                envelope
            ) VALUES (
                {interactionEvent.MessageId},
                {interactionEvent.MessageType},
                {interactionEvent.Version},
                {interactionEvent.Producer},
                {interactionEvent.OccurredAt},
                clock_timestamp(),
                {interactionEvent.CorrelationId},
                {interactionEvent.UserId},
                {interactionEvent.EquipmentListingId},
                {interactionEvent.EquipmentTypeId},
                {interactionEvent.ScoreWeight},
                {interactionEvent.Envelope}::jsonb
            )
            ON CONFLICT (message_id) DO NOTHING;
        ", cancellationToken);

        if (rowsAffected == 1)
        {
            return new InteractionIngestResult(InteractionIngestStatus.Inserted);
        }

        var matches = await _dbContext.Database
            .SqlQuery<bool>($@"
                SELECT (envelope = {interactionEvent.Envelope}::jsonb) AS ""Value""
                FROM recommendation.interaction_events
                WHERE message_id = {interactionEvent.MessageId}
            ")
            .ToListAsync(cancellationToken);

        if (matches is [true])
        {
            return new InteractionIngestResult(InteractionIngestStatus.DuplicateIgnored);
        }

        return new InteractionIngestResult(
            InteractionIngestStatus.ConflictMismatch,
            $"Message ID {interactionEvent.MessageId} already exists with differing payload.");
    }
}
