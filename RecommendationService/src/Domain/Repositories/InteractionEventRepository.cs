using System.Data;
using System.Data.Common;
using Dprental.RecommendationService.Domain.Entities;
using Dprental.RecommendationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        DbTransaction? currentTx = _dbContext.Database.CurrentTransaction?.GetDbTransaction();

        // 1. Atomic insert with ON CONFLICT (message_id) DO NOTHING RETURNING message_id
        const string insertSql = @"
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
                @p0,
                @p1,
                @p2,
                @p3,
                @p4,
                clock_timestamp(),
                @p5,
                @p6,
                @p7,
                @p8,
                @p9,
                @p10::jsonb
            )
            ON CONFLICT (message_id) DO NOTHING
            RETURNING message_id;
        ";

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = currentTx;
            cmd.CommandText = insertSql;

            AddParam(cmd, "@p0", interactionEvent.MessageId);
            AddParam(cmd, "@p1", interactionEvent.MessageType);
            AddParam(cmd, "@p2", interactionEvent.Version);
            AddParam(cmd, "@p3", interactionEvent.Producer);
            AddParam(cmd, "@p4", interactionEvent.OccurredAt);
            AddParam(cmd, "@p5", (object?)interactionEvent.CorrelationId ?? DBNull.Value);
            AddParam(cmd, "@p6", (object?)interactionEvent.UserId ?? DBNull.Value);
            AddParam(cmd, "@p7", (object?)interactionEvent.EquipmentListingId ?? DBNull.Value);
            AddParam(cmd, "@p8", (object?)interactionEvent.EquipmentTypeId ?? DBNull.Value);
            AddParam(cmd, "@p9", interactionEvent.ScoreWeight);
            AddParam(cmd, "@p10", interactionEvent.Envelope);

            var insertedId = await cmd.ExecuteScalarAsync(cancellationToken);
            if (insertedId != null && insertedId != DBNull.Value)
            {
                return new InteractionIngestResult(InteractionIngestStatus.Inserted);
            }
        }

        // 2. Row exists: evaluate duplicate vs conflict via jsonb equality
        const string checkSql = @"
            SELECT (envelope = @p1::jsonb) AS is_identical
            FROM recommendation.interaction_events
            WHERE message_id = @p0;
        ";

        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.Transaction = currentTx;
            checkCmd.CommandText = checkSql;

            AddParam(checkCmd, "@p0", interactionEvent.MessageId);
            AddParam(checkCmd, "@p1", interactionEvent.Envelope);

            var result = await checkCmd.ExecuteScalarAsync(cancellationToken);
            bool isIdentical = result is true;

            if (isIdentical)
            {
                return new InteractionIngestResult(InteractionIngestStatus.DuplicateIgnored);
            }

            return new InteractionIngestResult(
                InteractionIngestStatus.ConflictMismatch,
                $"Message ID {interactionEvent.MessageId} already exists with differing payload.");
        }
    }

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }
}
