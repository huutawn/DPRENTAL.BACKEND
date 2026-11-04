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
}
