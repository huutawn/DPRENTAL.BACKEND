using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class RepositoriesTests
{
    private static ReviewServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new ReviewServiceDbContext(options);
    }

    [Fact]
    public async Task ReviewRepository_AddAndGetByIdAsync_PersistsAndRetrieves()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var review = Review.Create(
            id: reviewId,
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 5,
            comment: "Great experience",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        await repository.AddAsync(review);

        var fetched = await repository.GetByIdAsync(reviewId);
        Assert.NotNull(fetched);
        Assert.Equal(reviewId, fetched.Id);
        Assert.Equal(5, fetched.Rating);
        Assert.Equal(ReviewDirection.renter_to_supplier_listing, fetched.Direction);
    }

    [Fact]
    public async Task ReviewRepository_ExistsByItemAndDirectionAsync_ReturnsExpectedResult()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var itemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var review = Review.Create(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: itemId,
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 4,
            comment: "Nice",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        await repository.AddAsync(review);

        // Enum overload
        Assert.True(await repository.ExistsByItemAndDirectionAsync(itemId, ReviewDirection.renter_to_supplier_listing));
        Assert.False(await repository.ExistsByItemAndDirectionAsync(itemId, ReviewDirection.supplier_to_renter));
        Assert.False(await repository.ExistsByItemAndDirectionAsync(Guid.NewGuid(), ReviewDirection.renter_to_supplier_listing));

        // String overload
        Assert.True(await repository.ExistsByItemAndDirectionAsync(itemId, "renter_to_supplier_listing"));
        Assert.False(await repository.ExistsByItemAndDirectionAsync(itemId, "supplier_to_renter"));
        Assert.False(await repository.ExistsByItemAndDirectionAsync(itemId, "invalid_direction"));
    }

    [Fact]
    public async Task ReviewRepository_GetByItemAndDirectionAsync_ReturnsEntityWhenExists()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var itemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var review = Review.Create(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: itemId,
            direction: ReviewDirection.supplier_to_renter,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 5,
            comment: "Clean customer",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        await repository.AddAsync(review);

        var found = await repository.GetByItemAndDirectionAsync(itemId, ReviewDirection.supplier_to_renter);
        Assert.NotNull(found);
        Assert.Equal(itemId, found.ItemId);
        Assert.Equal(ReviewDirection.supplier_to_renter, found.Direction);

        var notFound = await repository.GetByItemAndDirectionAsync(itemId, ReviewDirection.renter_to_supplier_listing);
        Assert.Null(notFound);
    }

    [Fact]
    public async Task ReviewRepository_UpdateAsync_SavesChanges()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var review = Review.Create(
            id: reviewId,
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 3,
            comment: "Initial comment",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        await repository.AddAsync(review);

        review.Update(4, "Updated comment", now.AddHours(1));
        await repository.UpdateAsync(review);

        var updated = await repository.GetByIdAsync(reviewId);
        Assert.NotNull(updated);
        Assert.Equal(4, updated.Rating);
        Assert.Equal("Updated comment", updated.Comment);
        Assert.Equal(2L, updated.Version);
    }

    [Fact]
    public async Task ReviewAuditRepository_AddAndGetByReviewIdAsync_WorksCorrectly()
    {
        using var context = CreateDbContext();
        var repository = new ReviewAuditRepository(context);

        var reviewId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var audit1 = new ReviewAudit(
            id: Guid.NewGuid(),
            reviewId: reviewId,
            actorId: Guid.NewGuid(),
            actorRole: "Renter",
            action: ReviewAuditAction.Created,
            reason: null,
            oldValues: null,
            newValues: "{\"rating\":4}",
            createdAt: now);

        var audit2 = new ReviewAudit(
            id: Guid.NewGuid(),
            reviewId: reviewId,
            actorId: Guid.NewGuid(),
            actorRole: "Renter",
            action: ReviewAuditAction.Updated,
            reason: "Fix rating",
            oldValues: "{\"rating\":4}",
            newValues: "{\"rating\":5}",
            createdAt: now.AddMinutes(5));

        await repository.AddAsync(audit1);
        await repository.AddAsync(audit2);

        var audits = await repository.GetByReviewIdAsync(reviewId);
        Assert.Equal(2, audits.Count);
        // Ordered by CreatedAt DESC
        Assert.Equal(ReviewAuditAction.Updated, audits[0].Action);
        Assert.Equal(ReviewAuditAction.Created, audits[1].Action);
    }

    [Fact]
    public async Task IdempotencyRepository_SaveAndGetAsync_WorksCorrectly()
    {
        using var context = CreateDbContext();
        var repository = new IdempotencyRepository(context);

        var actorId = Guid.NewGuid();
        var key = "test-key-123";
        var now = DateTimeOffset.UtcNow;

        var record = new IdempotencyRecord(
            id: Guid.NewGuid(),
            actorId: actorId,
            idempotencyKey: key,
            requestFingerprint: "fingerprint-1",
            httpMethod: "POST",
            requestPath: "/api/v1/reviews",
            responseStatusCode: 201,
            responseHeaders: null,
            responseBody: "{\"status\":\"ok\"}",
            createdAt: now,
            expiresAt: now.AddDays(30));

        await repository.SaveAsync(record);

        // Get by actorId and key
        var fetched1 = await repository.GetAsync(actorId, key);
        Assert.NotNull(fetched1);
        Assert.Equal(201, fetched1.ResponseStatusCode);

        // Get by key and actorId
        var fetched2 = await repository.GetAsync(key, actorId);
        Assert.NotNull(fetched2);
        Assert.Equal(201, fetched2.ResponseStatusCode);

        // Get by key
        var fetched3 = await repository.GetAsync(key);
        Assert.NotNull(fetched3);
        Assert.Equal(201, fetched3.ResponseStatusCode);

        // Non-existent key returns null
        Assert.Null(await repository.GetAsync(actorId, "non-existent"));
        Assert.Null(await repository.GetAsync("non-existent"));
    }

    [Fact]
    public async Task IdempotencyRepository_SaveAsync_UpdatesExistingRecord()
    {
        using var context = CreateDbContext();
        var repository = new IdempotencyRepository(context);

        var actorId = Guid.NewGuid();
        var key = "update-key-456";
        var now = DateTimeOffset.UtcNow;

        var record = new IdempotencyRecord(
            id: Guid.NewGuid(),
            actorId: actorId,
            idempotencyKey: key,
            requestFingerprint: "fingerprint-initial",
            httpMethod: "POST",
            requestPath: "/api/v1/reviews",
            responseStatusCode: 200,
            responseHeaders: null,
            responseBody: "{\"initial\":true}",
            createdAt: now,
            expiresAt: now.AddDays(30));

        await repository.SaveAsync(record);

        record.ResponseBody = "{\"updated\":true}";
        record.ResponseStatusCode = 201;
        await repository.SaveAsync(record);

        var fetched = await repository.GetAsync(actorId, key);
        Assert.NotNull(fetched);
        Assert.Equal(201, fetched.ResponseStatusCode);
        Assert.Equal("{\"updated\":true}", fetched.ResponseBody);
    }

    [Fact]
    public void ServiceCollectionExtensions_RegistersAllRepositories()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ReviewServiceDbContext>(options =>
            options.UseInMemoryDatabase("di_test"));
        services.AddReviewServiceRepositories();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IReviewRepository>());
        Assert.NotNull(provider.GetService<IReviewAuditRepository>());
        Assert.NotNull(provider.GetService<IIdempotencyRepository>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddReviewServiceDomainRepositories_ThrowsInvalidOperationException_WhenConnectionStringEmpty(string? emptyConnectionString)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddReviewServiceDomainRepositories(emptyConnectionString));
    }
}
