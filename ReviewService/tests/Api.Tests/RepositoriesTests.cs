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

    [Fact]
    public async Task ReviewRepository_GetPublicListingReviewsAsync_EnforcesFiltersAndPagination()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var targetListingId = Guid.NewGuid();
        var otherListingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var author1 = Guid.NewGuid();
        var author2 = Guid.NewGuid();
        var author3 = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        // Valid 1 (earlier)
        var rev1 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, author1, supplierId, targetListingId,
            5, "Valid review 1", baseTime.AddHours(-3), baseTime.AddDays(7));

        // Valid 2 (later)
        var rev2 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, author2, supplierId, targetListingId,
            4, "Valid review 2", baseTime.AddHours(-1), baseTime.AddDays(7));

        // Valid 3 (middle)
        var rev3 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, author3, supplierId, targetListingId,
            3, "Valid review 3", baseTime.AddHours(-2), baseTime.AddDays(7));

        // Filter: different listing
        var otherListingRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, otherListingId,
            5, "Other listing", baseTime, baseTime.AddDays(7));

        // Filter: supplier_to_renter
        var wrongDirectionRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.supplier_to_renter, Guid.NewGuid(), supplierId, targetListingId,
            5, "Wrong direction", baseTime, baseTime.AddDays(7));

        // Filter: Withdrawn
        var withdrawnRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, targetListingId,
            5, "Withdrawn review", baseTime, baseTime.AddDays(7));
        withdrawnRev.AuthorState = AuthorState.Withdrawn;

        // Filter: Hidden
        var hiddenRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, targetListingId,
            5, "Hidden review", baseTime, baseTime.AddDays(7));
        hiddenRev.ModerationState = ModerationState.Hidden;

        // Filter: Withdrawn + Visible
        var withdrawnVisibleRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, targetListingId,
            5, "Withdrawn visible review", baseTime, baseTime.AddDays(7));
        withdrawnVisibleRev.AuthorState = AuthorState.Withdrawn;
        withdrawnVisibleRev.ModerationState = ModerationState.Visible;

        await repository.AddAsync(rev1);
        await repository.AddAsync(rev2);
        await repository.AddAsync(rev3);
        await repository.AddAsync(otherListingRev);
        await repository.AddAsync(wrongDirectionRev);
        await repository.AddAsync(withdrawnRev);
        await repository.AddAsync(hiddenRev);
        await repository.AddAsync(withdrawnVisibleRev);

        // Page 1 with pageSize 2 (Expected order: rev2 [most recent], then rev3, then rev1)
        var (page1Items, totalCount) = await repository.GetPublicListingReviewsAsync(targetListingId, 1, 2);
        Assert.Equal(3, totalCount);
        Assert.Equal(2, page1Items.Count);
        Assert.Equal(rev2.Id, page1Items[0].Id);
        Assert.Equal(rev3.Id, page1Items[1].Id);

        // Page 2 with pageSize 2
        var (page2Items, totalCount2) = await repository.GetPublicListingReviewsAsync(targetListingId, 2, 2);
        Assert.Equal(3, totalCount2);
        Assert.Single(page2Items);
        Assert.Equal(rev1.Id, page2Items[0].Id);

        // Non-existent listing returns 0 items and 0 count
        var (emptyItems, emptyCount) = await repository.GetPublicListingReviewsAsync(Guid.NewGuid(), 1, 10);
        Assert.Empty(emptyItems);
        Assert.Equal(0, emptyCount);
    }

    [Fact]
    public async Task ReviewRepository_GetPublicSupplierReviewsAsync_EnforcesFiltersAndPagination()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var targetSupplierId = Guid.NewGuid();
        var otherSupplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        var rev1 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), targetSupplierId, listingId,
            5, "Valid supplier rev 1", baseTime.AddMinutes(-10), baseTime.AddDays(7));

        var rev2 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), targetSupplierId, listingId,
            4, "Valid supplier rev 2", baseTime, baseTime.AddDays(7));

        var otherSupplierRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), otherSupplierId, listingId,
            5, "Other supplier", baseTime, baseTime.AddDays(7));

        var wrongDirectionRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.supplier_to_renter, Guid.NewGuid(), targetSupplierId, listingId,
            5, "Wrong direction", baseTime, baseTime.AddDays(7));

        var withdrawnRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), targetSupplierId, listingId,
            5, "Withdrawn", baseTime, baseTime.AddDays(7));
        withdrawnRev.AuthorState = AuthorState.Withdrawn;

        var hiddenRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), targetSupplierId, listingId,
            5, "Hidden", baseTime, baseTime.AddDays(7));
        hiddenRev.ModerationState = ModerationState.Hidden;

        await repository.AddAsync(rev1);
        await repository.AddAsync(rev2);
        await repository.AddAsync(otherSupplierRev);
        await repository.AddAsync(wrongDirectionRev);
        await repository.AddAsync(withdrawnRev);
        await repository.AddAsync(hiddenRev);

        var (items, totalCount) = await repository.GetPublicSupplierReviewsAsync(targetSupplierId, 1, 10);
        Assert.Equal(2, totalCount);
        Assert.Equal(2, items.Count);
        Assert.Equal(rev2.Id, items[0].Id);
        Assert.Equal(rev1.Id, items[1].Id);
    }

    [Fact]
    public async Task ReviewRepository_GetMyReviewsAsync_ReturnsAllAuthorReviewsIncludingWithdrawnAndHidden()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var authorId = Guid.NewGuid();
        var otherAuthorId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        var activeRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, Guid.NewGuid(), Guid.NewGuid(),
            5, "Active review", baseTime.AddMinutes(-20), baseTime.AddDays(7));

        var withdrawnRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, Guid.NewGuid(), Guid.NewGuid(),
            3, "Withdrawn review", baseTime.AddMinutes(-10), baseTime.AddDays(7));
        withdrawnRev.AuthorState = AuthorState.Withdrawn;

        var hiddenRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.supplier_to_renter, authorId, Guid.NewGuid(), Guid.NewGuid(),
            2, "Hidden review", baseTime, baseTime.AddDays(7));
        hiddenRev.ModerationState = ModerationState.Hidden;

        var otherAuthorRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, otherAuthorId, Guid.NewGuid(), Guid.NewGuid(),
            4, "Other author", baseTime, baseTime.AddDays(7));

        await repository.AddAsync(activeRev);
        await repository.AddAsync(withdrawnRev);
        await repository.AddAsync(hiddenRev);
        await repository.AddAsync(otherAuthorRev);

        var (items, totalCount) = await repository.GetMyReviewsAsync(authorId, 1, 10);
        Assert.Equal(3, totalCount);
        Assert.Equal(3, items.Count);
        Assert.Equal(hiddenRev.Id, items[0].Id);
        Assert.Equal(withdrawnRev.Id, items[1].Id);
        Assert.Equal(activeRev.Id, items[2].Id);
    }

    [Fact]
    public async Task ReviewRepository_GetRatingSummaryAsync_WhenNoReviews_ReturnsNullAverageAndZeroDistribution()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var targetId = Guid.NewGuid();

        var (listingCount, listingAvg, listingDist) = await repository.GetRatingSummaryAsync("listing", targetId);
        Assert.Equal(0, listingCount);
        Assert.Null(listingAvg);
        Assert.Equal(5, listingDist.Count);
        for (var star = 1; star <= 5; star++)
        {
            Assert.True(listingDist.ContainsKey(star));
            Assert.Equal(0, listingDist[star]);
        }

        var (supplierCount, supplierAvg, supplierDist) = await repository.GetRatingSummaryAsync("supplier", targetId);
        Assert.Equal(0, supplierCount);
        Assert.Null(supplierAvg);
        Assert.Equal(5, supplierDist.Count);
        for (var star = 1; star <= 5; star++)
        {
            Assert.True(supplierDist.ContainsKey(star));
            Assert.Equal(0, supplierDist[star]);
        }
    }

    [Fact]
    public async Task ReviewRepository_GetRatingSummaryAsync_CalculatesSummaryCorrectlyWithRoundingAwayFromZero()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var listingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        var rev1 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            5, "Five star 1", baseTime, baseTime.AddDays(7));

        var rev2 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            5, "Five star 2", baseTime, baseTime.AddDays(7));

        var rev3 = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            4, "Four star", baseTime, baseTime.AddDays(7));

        var withdrawnRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            1, "Withdrawn", baseTime, baseTime.AddDays(7));
        withdrawnRev.AuthorState = AuthorState.Withdrawn;

        var hiddenRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            1, "Hidden", baseTime, baseTime.AddDays(7));
        hiddenRev.ModerationState = ModerationState.Hidden;

        var wrongDirectionRev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.supplier_to_renter, Guid.NewGuid(), supplierId, listingId,
            1, "Wrong dir", baseTime, baseTime.AddDays(7));

        await repository.AddAsync(rev1);
        await repository.AddAsync(rev2);
        await repository.AddAsync(rev3);
        await repository.AddAsync(withdrawnRev);
        await repository.AddAsync(hiddenRev);
        await repository.AddAsync(wrongDirectionRev);

        var (count, avg, dist) = await repository.GetRatingSummaryAsync("listing", listingId);

        Assert.Equal(3, count);
        Assert.NotNull(avg);
        Assert.Equal(4.7, avg.Value);
        Assert.Equal(0, dist[1]);
        Assert.Equal(0, dist[2]);
        Assert.Equal(0, dist[3]);
        Assert.Equal(1, dist[4]);
        Assert.Equal(2, dist[5]);

        var (supCount, supAvg, supDist) = await repository.GetRatingSummaryAsync("supplier", supplierId);
        Assert.Equal(3, supCount);
        Assert.Equal(4.7, supAvg);
        Assert.Equal(1, supDist[4]);
        Assert.Equal(2, supDist[5]);
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("LISTING")]
    [InlineData("Listing")]
    [InlineData("supplier")]
    [InlineData("SUPPLIER")]
    [InlineData("Supplier")]
    public async Task ReviewRepository_GetRatingSummaryAsync_AcceptsCaseInsensitiveTargetType(string targetType)
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var targetId = Guid.NewGuid();
        var (count, avg, dist) = await repository.GetRatingSummaryAsync(targetType, targetId);

        Assert.Equal(0, count);
        Assert.Null(avg);
        Assert.NotNull(dist);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("order")]
    public async Task ReviewRepository_GetRatingSummaryAsync_ThrowsArgumentException_WhenTargetTypeInvalid(string? invalidTargetType)
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.GetRatingSummaryAsync(invalidTargetType!, Guid.NewGuid()));
    }

    [Fact]
    public async Task ReviewRepository_GetRatingSummaryAsync_MidpointRoundingAwayFromZero_RoundsUpOnPointFive()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var listingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < 17; i++)
        {
            var rev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
                5, "5 stars", baseTime, baseTime.AddDays(7));
            await repository.AddAsync(rev);
        }

        for (var i = 0; i < 3; i++)
        {
            var rev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
                4, "4 stars", baseTime, baseTime.AddDays(7));
            await repository.AddAsync(rev);
        }

        var (count, avg, dist) = await repository.GetRatingSummaryAsync("listing", listingId);
        Assert.Equal(20, count);
        Assert.Equal(4.9, avg);
        Assert.Equal(17, dist[5]);
        Assert.Equal(3, dist[4]);
    }

    [Fact]
    public async Task ReviewRepository_Queries_OrderByIdDescending_WhenCreatedAtIsIdentical()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var sameTime = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var listingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        var idSmaller = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idLarger = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var rev1 = Review.Create(idSmaller, Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, supplierId, listingId,
            5, "Rev 1", sameTime, sameTime.AddDays(7));

        var rev2 = Review.Create(idLarger, Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, supplierId, listingId,
            4, "Rev 2", sameTime, sameTime.AddDays(7));

        await repository.AddAsync(rev1);
        await repository.AddAsync(rev2);

        // Listing: larger ID should come first
        var (listingItems, _) = await repository.GetPublicListingReviewsAsync(listingId, 1, 10);
        Assert.Equal(2, listingItems.Count);
        Assert.Equal(idLarger, listingItems[0].Id);
        Assert.Equal(idSmaller, listingItems[1].Id);

        // Supplier: larger ID should come first
        var (supplierItems, _) = await repository.GetPublicSupplierReviewsAsync(supplierId, 1, 10);
        Assert.Equal(2, supplierItems.Count);
        Assert.Equal(idLarger, supplierItems[0].Id);
        Assert.Equal(idSmaller, supplierItems[1].Id);

        // My reviews: larger ID should come first
        var (myItems, _) = await repository.GetMyReviewsAsync(authorId, 1, 10);
        Assert.Equal(2, myItems.Count);
        Assert.Equal(idLarger, myItems[0].Id);
        Assert.Equal(idSmaller, myItems[1].Id);
    }

    [Fact]
    public async Task ReviewRepository_GetPublicSupplierReviewsAndMyReviews_SupportPagination()
    {
        using var context = CreateDbContext();
        var repository = new ReviewRepository(context);

        var supplierId = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            var rev = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                ReviewDirection.renter_to_supplier_listing, authorId, supplierId, Guid.NewGuid(),
                5, $"Review {i}", baseTime.AddHours(-i), baseTime.AddDays(7));
            await repository.AddAsync(rev);
        }

        // Supplier pagination: page 2, pageSize 2 -> items 2 and 3
        var (supplierPage2, supplierTotal) = await repository.GetPublicSupplierReviewsAsync(supplierId, 2, 2);
        Assert.Equal(5, supplierTotal);
        Assert.Equal(2, supplierPage2.Count);

        // My reviews pagination: page 3, pageSize 2 -> item 4
        var (myPage3, myTotal) = await repository.GetMyReviewsAsync(authorId, 3, 2);
        Assert.Equal(5, myTotal);
        Assert.Single(myPage3);
    }
}
