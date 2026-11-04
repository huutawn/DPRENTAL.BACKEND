using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class DbContextConfigurationTests
{
    private static ReviewServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new ReviewServiceDbContext(options);
    }

    [Fact]
    public void DbContext_ConfiguresTableNamesCorrectly()
    {
        using var context = CreateDbContext();
        var model = context.Model;

        var reviewEntity = model.FindEntityType(typeof(Review));
        Assert.NotNull(reviewEntity);
        Assert.Equal("reviews", reviewEntity.GetTableName());

        var auditEntity = model.FindEntityType(typeof(ReviewAudit));
        Assert.NotNull(auditEntity);
        Assert.Equal("review_audits", auditEntity.GetTableName());

        var idempotencyEntity = model.FindEntityType(typeof(IdempotencyRecord));
        Assert.NotNull(idempotencyEntity);
        Assert.Equal("idempotency_records", idempotencyEntity.GetTableName());
    }

    [Fact]
    public void DbContext_ConfiguresConcurrencyTokenOnReviewVersion()
    {
        using var context = CreateDbContext();
        var reviewEntity = context.Model.FindEntityType(typeof(Review));
        Assert.NotNull(reviewEntity);

        var versionProperty = reviewEntity.FindProperty(nameof(Review.Version));
        Assert.NotNull(versionProperty);
        Assert.True(versionProperty.IsConcurrencyToken);
        Assert.Equal("version", versionProperty.GetColumnName());
    }

    [Fact]
    public void DbContext_ConfiguresReviewCheckConstraints()
    {
        using var context = CreateDbContext();
        var designModel = context.GetService<IDesignTimeModel>().Model;
        var reviewEntity = designModel.FindEntityType(typeof(Review));
        Assert.NotNull(reviewEntity);
        var checkConstraints = reviewEntity!.GetCheckConstraints().ToList();

        Assert.Contains(checkConstraints, c => c.Name == "chk_reviews_direction");
        Assert.Contains(checkConstraints, c => c.Name == "chk_reviews_rating");
        Assert.Contains(checkConstraints, c => c.Name == "chk_reviews_author_state");
        Assert.Contains(checkConstraints, c => c.Name == "chk_reviews_moderation_state");
    }

    [Fact]
    public void DbContext_ConfiguresAuditCheckConstraint()
    {
        using var context = CreateDbContext();
        var designModel = context.GetService<IDesignTimeModel>().Model;
        var auditEntity = designModel.FindEntityType(typeof(ReviewAudit));
        Assert.NotNull(auditEntity);
        var checkConstraints = auditEntity!.GetCheckConstraints().ToList();
        Assert.Contains(checkConstraints, c => c.Name == "chk_audits_action");
    }

    [Fact]
    public void DbContext_ConfiguresUniqueIndexOnItemAndDirection()
    {
        using var context = CreateDbContext();
        var reviewEntity = context.Model.FindEntityType(typeof(Review));
        Assert.NotNull(reviewEntity);

        var indexes = reviewEntity.GetIndexes().ToList();
        var uniqueIndex = indexes.FirstOrDefault(i =>
            i.IsUnique &&
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(Review.ItemId)) &&
            i.Properties.Any(p => p.Name == nameof(Review.Direction)));

        Assert.NotNull(uniqueIndex);
        Assert.Equal("uq_reviews_item_direction", uniqueIndex.GetDatabaseName());
    }

    [Fact]
    public void DbContext_ConfiguresForeignKeyFromAuditToReview()
    {
        using var context = CreateDbContext();
        var auditEntity = context.Model.FindEntityType(typeof(ReviewAudit));
        Assert.NotNull(auditEntity);

        var foreignKeys = auditEntity.GetForeignKeys().ToList();
        var fk = foreignKeys.FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(Review));

        Assert.NotNull(fk);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal("fk_audits_review", fk.GetConstraintName());
    }

    [Fact]
    public void DbContext_ConfiguresUniqueIndexOnIdempotencyActorAndKey()
    {
        using var context = CreateDbContext();
        var idempotencyEntity = context.Model.FindEntityType(typeof(IdempotencyRecord));
        Assert.NotNull(idempotencyEntity);

        var indexes = idempotencyEntity.GetIndexes().ToList();
        var uniqueIndex = indexes.FirstOrDefault(i =>
            i.IsUnique &&
            i.Properties.Count == 2 &&
            i.Properties.Any(p => p.Name == nameof(IdempotencyRecord.ActorId)) &&
            i.Properties.Any(p => p.Name == nameof(IdempotencyRecord.IdempotencyKey)));

        Assert.NotNull(uniqueIndex);
        Assert.Equal("uq_idempotency_actor_key", uniqueIndex.GetDatabaseName());
    }

    [Fact]
    public void DbContext_ConfiguresConcurrencyTokenOnReviewReportVersion()
    {
        using var context = CreateDbContext();
        var reportEntity = context.Model.FindEntityType(typeof(ReviewReport));
        Assert.NotNull(reportEntity);

        var versionProperty = reportEntity.FindProperty(nameof(ReviewReport.Version));
        Assert.NotNull(versionProperty);
        Assert.True(versionProperty.IsConcurrencyToken);
        Assert.Equal("version", versionProperty.GetColumnName());
    }

    [Fact]
    public void DbContext_ConfiguresForeignKeysForReportAndMediaToReview()
    {
        using var context = CreateDbContext();

        // 1. Report -> Review FK
        var reportEntity = context.Model.FindEntityType(typeof(ReviewReport));
        Assert.NotNull(reportEntity);
        var reportFk = reportEntity.GetForeignKeys().FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(Review));
        Assert.NotNull(reportFk);
        Assert.Equal(DeleteBehavior.Restrict, reportFk.DeleteBehavior);
        Assert.Equal("fk_reports_review", reportFk.GetConstraintName());

        // 2. Media -> Review FK
        var mediaEntity = context.Model.FindEntityType(typeof(ReviewMedia));
        Assert.NotNull(mediaEntity);
        var mediaFk = mediaEntity.GetForeignKeys().FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(Review));
        Assert.NotNull(mediaFk);
        Assert.Equal(DeleteBehavior.Cascade, mediaFk.DeleteBehavior);
        Assert.Equal("fk_review_media_review", mediaFk.GetConstraintName());
    }

    [Fact]
    public void DbContext_ConfiguresOutboxMessagesCorrectly()
    {
        using var context = CreateDbContext();
        var model = context.Model;
        var outboxEntity = model.FindEntityType(typeof(OutboxMessage));
        Assert.NotNull(outboxEntity);
        Assert.Equal("outbox_messages", outboxEntity.GetTableName());

        Assert.Equal("claim_token", outboxEntity.FindProperty(nameof(OutboxMessage.ClaimToken))?.GetColumnName());
        Assert.Equal("lease_expires_at", outboxEntity.FindProperty(nameof(OutboxMessage.LeaseExpiresAt))?.GetColumnName());
        Assert.Equal("claim_owner", outboxEntity.FindProperty(nameof(OutboxMessage.ClaimOwner))?.GetColumnName());
        Assert.Equal("attempts", outboxEntity.FindProperty(nameof(OutboxMessage.Attempts))?.GetColumnName());
        Assert.Equal("next_attempt_at", outboxEntity.FindProperty(nameof(OutboxMessage.NextAttemptAt))?.GetColumnName());
        Assert.Equal("last_error_code", outboxEntity.FindProperty(nameof(OutboxMessage.LastErrorCode))?.GetColumnName());
        Assert.Equal("delivery_state", outboxEntity.FindProperty(nameof(OutboxMessage.DeliveryState))?.GetColumnName());

        var claimIndex = outboxEntity.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "idx_outbox_claim");
        Assert.NotNull(claimIndex);
        var indexPropNames = claimIndex.Properties.Select(p => p.Name).ToList();
        Assert.Equal(new[] { nameof(OutboxMessage.DeliveryState), nameof(OutboxMessage.NextAttemptAt), nameof(OutboxMessage.LeaseExpiresAt) }, indexPropNames);
    }
}
