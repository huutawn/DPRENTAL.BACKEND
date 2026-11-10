using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Services;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class ReviewServiceQueryTests
{
    private static ReviewServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReviewServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new ReviewServiceDbContext(options);
    }

    private static (ReviewQueryService Service, ReviewRepository Repository, TestTimeProvider TimeProvider) CreateService(ReviewServiceDbContext dbContext)
    {
        var repository = new ReviewRepository(dbContext);
        var projectionRepository = new ReviewProjectionRepository(dbContext);
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

        var service = new ReviewQueryService(
            repository,
            projectionRepository,
            timeProvider,
            NullLogger<ReviewQueryService>.Instance);

        return (service, repository, timeProvider);
    }

    [Fact]
    public async Task GetListingReviewsAsync_ReturnsValidationError_WhenListingIdIsEmpty()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var result = await service.GetListingReviewsAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_LISTING_ID", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetListingReviewsAsync_ReturnsEmptyResult_WhenNoReviewsExist()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var listingId = Guid.NewGuid();
        var result = await service.GetListingReviewsAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, result.Value.TotalCount);
        Assert.Equal(0, result.Value.TotalPages);
        Assert.Equal(1, result.Value.Page);
        Assert.Equal(20, result.Value.PageSize);
    }

    [Fact]
    public async Task GetListingReviewsAsync_ReturnsPublicReviews_WithNeutralAuthorMaskedName()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);

        var listingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        // Active + Visible + renter_to_supplier_listing (valid)
        var validReview = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            5, "Thiết bị tuyệt vời", now, now.AddDays(7));
        await repository.AddAsync(validReview);

        // Withdrawn (should be hidden)
        var withdrawn = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            4, "Đã rút", now, now.AddDays(7));
        withdrawn.Withdraw(now);
        await repository.AddAsync(withdrawn);

        // Hidden by admin (should be hidden)
        var hidden = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
            1, "Spam", now, now.AddDays(7));
        hidden.AdminHide("Spam", now);
        await repository.AddAsync(hidden);

        // Reverse direction (supplier_to_renter, should be hidden)
        var reverse = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.supplier_to_renter, supplierId, Guid.NewGuid(), listingId,
            5, "Khách thuê tốt", now, now.AddDays(7));
        await repository.AddAsync(reverse);

        var result = await service.GetListingReviewsAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.Equal(1, result.Value.TotalPages);

        var item = result.Value.Items[0];
        Assert.Equal(validReview.Id, item.ReviewId);
        Assert.Equal(5, item.Rating);
        Assert.Equal("Thiết bị tuyệt vời", item.Comment);
        Assert.Equal("Khách hàng", item.AuthorMaskedName);
    }

    [Fact]
    public async Task GetSupplierReviewsAsync_ReturnsValidationError_WhenSupplierIdIsEmpty()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var result = await service.GetSupplierReviewsAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_SUPPLIER_ID", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetSupplierReviewsAsync_ReturnsPublicReviews_ForSpecifiedSupplier()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);

        var supplierId = Guid.NewGuid();
        var otherSupplierId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        var rev1 = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, Guid.NewGuid(),
            5, "Supplier tốt", now, now.AddDays(7));
        var rev2 = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), otherSupplierId, Guid.NewGuid(),
            4, "Other supplier", now, now.AddDays(7));

        await repository.AddAsync(rev1);
        await repository.AddAsync(rev2);

        var result = await service.GetSupplierReviewsAsync(supplierId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Items);
        Assert.Equal(rev1.Id, result.Value.Items[0].ReviewId);
        Assert.Equal("Khách hàng", result.Value.Items[0].AuthorMaskedName);
    }

    [Fact]
    public async Task GetMyReviewsAsync_ReturnsUnauthorized_WhenActorIdIsEmpty()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var result = await service.GetMyReviewsAsync(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task GetMyReviewsAsync_ReturnsAuthorReviews_IncludingWithdrawn()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);

        var authorId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        var activeRev = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, Guid.NewGuid(), Guid.NewGuid(),
            5, "Đang active", now, now.AddDays(7));

        var withdrawnRev = Review.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            ReviewDirection.renter_to_supplier_listing, authorId, Guid.NewGuid(), Guid.NewGuid(),
            3, "Đã rút", now, now.AddDays(7));
        withdrawnRev.Withdraw(now);

        await repository.AddAsync(activeRev);
        await repository.AddAsync(withdrawnRev);

        var result = await service.GetMyReviewsAsync(authorId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal(2, result.Value.Items.Count);

        var withdrawnItem = result.Value.Items.FirstOrDefault(x => x.ReviewId == withdrawnRev.Id);
        Assert.NotNull(withdrawnItem);
        Assert.Equal("Withdrawn", withdrawnItem.AuthorState);
    }

    [Fact]
    public async Task GetMyReviewsAsync_GuidActorId_ValidatesActorProperly()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        // Empty GUID
        var emptyResult = await service.GetMyReviewsAsync(Guid.Empty, 1, 20);
        Assert.True(emptyResult.IsFailure);
        Assert.Equal("UNAUTHORIZED", emptyResult.Error.Code);

        // Valid GUID
        var validActorId = Guid.NewGuid();
        var validResult = await service.GetMyReviewsAsync(validActorId, 1, 20);
        Assert.True(validResult.IsSuccess);
    }
    [Fact]
    public async Task GetMyReviewsAsync_ReviewActorContextOverload_Works()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var actorId = Guid.NewGuid();
        var context = new ReviewActorContext(actorId, "user", false);

        var result = await service.GetMyReviewsAsync(context, 1, 20);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("LISTING")]
    [InlineData("Listing")]
    [InlineData("supplier")]
    [InlineData("SUPPLIER")]
    [InlineData("Supplier")]
    public async Task GetSummaryAsync_AcceptsCaseInsensitiveTargetType(string targetType)
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var targetId = Guid.NewGuid();
        var result = await service.GetSummaryAsync(targetType, targetId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(targetId, result.Value.TargetId);
        Assert.Null(result.Value.AverageRating);
        Assert.Equal(0, result.Value.ReviewCount);
        Assert.Equal(5, result.Value.RatingDistribution.Count);
        Assert.Equal(0, result.Value.RatingDistribution["5"]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("order")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task GetSummaryAsync_ReturnsValidationError_WhenTargetTypeIsInvalid(string? invalidType)
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var result = await service.GetSummaryAsync(invalidType!, Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_TARGET_TYPE", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetSummaryAsync_ReturnsValidationError_WhenTargetIdIsEmpty()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var result = await service.GetSummaryAsync("listing", Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_TARGET_ID", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetSummaryAsync_CalculatesAverageAndDistribution_Correctly()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);

        var listingId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();

        // 12 x 5-star reviews
        for (int i = 0; i < 12; i++)
        {
            var r = Review.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
                5, "5 stars", now, now.AddDays(7));
            await repository.AddAsync(r);
        }

        // 3 x 4-star reviews
        for (int i = 0; i < 3; i++)
        {
            var r = Review.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                ReviewDirection.renter_to_supplier_listing, Guid.NewGuid(), supplierId, listingId,
                4, "4 stars", now, now.AddDays(7));
            await repository.AddAsync(r);
        }

        var result = await service.GetSummaryAsync("listing", listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal("listing", result.Value.TargetType);
        Assert.Equal(listingId, result.Value.TargetId);
        Assert.Equal(15, result.Value.ReviewCount);
        Assert.Equal(4.8, result.Value.AverageRating);
        Assert.Equal(12, result.Value.RatingDistribution["5"]);
        Assert.Equal(3, result.Value.RatingDistribution["4"]);
        Assert.Equal(0, result.Value.RatingDistribution["3"]);
        Assert.Equal(0, result.Value.RatingDistribution["2"]);
        Assert.Equal(0, result.Value.RatingDistribution["1"]);
    }

    #region Batch Review Status Tests

    [Fact]
    public async Task GetBatchReviewStatusAsync_EmptyActorId_ReturnsUnauthorized()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        var request = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid())
        ]);

        var result = await service.GetBatchReviewStatusAsync(Guid.Empty, request);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_NullOrEmptyItems_ReturnsValidationError()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);
        var actorId = Guid.NewGuid();

        var nullResult = await service.GetBatchReviewStatusAsync(actorId, new BatchReviewStatusRequest(null));
        Assert.True(nullResult.IsFailure);
        Assert.Equal("EMPTY_BATCH_ITEMS", nullResult.Error.Code);
        Assert.Equal(ErrorType.Validation, nullResult.Error.Type);

        var emptyResult = await service.GetBatchReviewStatusAsync(actorId, new BatchReviewStatusRequest([]));
        Assert.True(emptyResult.IsFailure);
        Assert.Equal("EMPTY_BATCH_ITEMS", emptyResult.Error.Code);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_Exceeds100Items_ReturnsValidationError()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);
        var actorId = Guid.NewGuid();

        var items = Enumerable.Range(1, 101)
            .Select(_ => new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var result = await service.GetBatchReviewStatusAsync(actorId, new BatchReviewStatusRequest(items));

        Assert.True(result.IsFailure);
        Assert.Equal("BATCH_LIMIT_EXCEEDED", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_EmptyGuidInItem_ReturnsValidationError()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);
        var actorId = Guid.NewGuid();

        var emptyOrderReq = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.Empty, Guid.NewGuid())
        ]);
        var res1 = await service.GetBatchReviewStatusAsync(actorId, emptyOrderReq);
        Assert.True(res1.IsFailure);
        Assert.Equal("INVALID_IDENTIFIER", res1.Error.Code);

        var emptyItemReq = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.NewGuid(), Guid.Empty)
        ]);
        var res2 = await service.GetBatchReviewStatusAsync(actorId, emptyItemReq);
        Assert.True(res2.IsFailure);
        Assert.Equal("INVALID_IDENTIFIER", res2.Error.Code);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_DuplicatePair_ReturnsValidationError()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);
        var actorId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var request = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(orderId, itemId),
            new ReviewStatusItemRequest(orderId, itemId)
        ]);

        var result = await service.GetBatchReviewStatusAsync(actorId, request);

        Assert.True(result.IsFailure);
        Assert.Equal("DUPLICATE_BATCH_ITEM", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_AllFiveStates_EvaluatedCorrectlyInExactRequestOrder()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);
        var actorId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var nowUtc = timeProvider.GetUtcNow();

        // 1. REVIEWED (Active review)
        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        var review1 = Review.Create(
            Guid.NewGuid(), order1, item1, ReviewDirection.renter_to_supplier_listing,
            actorId, supplierId, listingId, 5, "Active review", nowUtc.AddDays(-2), nowUtc.AddDays(5));
        await repository.AddAsync(review1);
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order1, item1, actorId, supplierId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), true, nowUtc.AddDays(-2)));

        // 2. REVIEWED (Withdrawn review)
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        var review2 = Review.Create(
            Guid.NewGuid(), order2, item2, ReviewDirection.renter_to_supplier_listing,
            actorId, supplierId, listingId, 4, "Withdrawn review", nowUtc.AddDays(-3), nowUtc.AddDays(4));
        review2.Withdraw(nowUtc.AddDays(-1));
        await repository.AddAsync(review2);
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order2, item2, actorId, supplierId, listingId,
            nowUtc.AddDays(-3), nowUtc.AddDays(27), true, nowUtc.AddDays(-3)));

        // 3. REVIEWED (Admin-hidden review)
        var order3 = Guid.NewGuid();
        var item3 = Guid.NewGuid();
        var review3 = Review.Create(
            Guid.NewGuid(), order3, item3, ReviewDirection.renter_to_supplier_listing,
            actorId, supplierId, listingId, 1, "Spam review", nowUtc.AddDays(-4), nowUtc.AddDays(3));
        review3.AdminHide("Spam violation", nowUtc.AddDays(-1));
        await repository.AddAsync(review3);
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order3, item3, actorId, supplierId, listingId,
            nowUtc.AddDays(-4), nowUtc.AddDays(26), true, nowUtc.AddDays(-4)));

        // 4. REVIEWED (Missing projection)
        var order4 = Guid.NewGuid();
        var item4 = Guid.NewGuid();
        var review4 = Review.Create(
            Guid.NewGuid(), order4, item4, ReviewDirection.renter_to_supplier_listing,
            actorId, supplierId, listingId, 5, "Review without projection", nowUtc.AddDays(-1), nowUtc.AddDays(6));
        await repository.AddAsync(review4);

        // 5. AVAILABLE (Valid projection, within deadline, not reviewed)
        var order5 = Guid.NewGuid();
        var item5 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order5, item5, actorId, supplierId, listingId,
            nowUtc.AddDays(-5), nowUtc.AddDays(25), true, nowUtc.AddDays(-5)));

        // 6. EXPIRED (nowUtc >= deadlineAt)
        var order6 = Guid.NewGuid();
        var item6 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order6, item6, actorId, supplierId, listingId,
            nowUtc.AddDays(-35), nowUtc.AddDays(-5), true, nowUtc.AddDays(-35)));

        // 7. NOT_ELIGIBLE (isEligible == false)
        var order7 = Guid.NewGuid();
        var item7 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order7, item7, actorId, supplierId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), false, nowUtc.AddDays(-2)));

        // 8. NOT_ELIGIBLE (renterId == supplierId)
        var order8 = Guid.NewGuid();
        var item8 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order8, item8, actorId, actorId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), true, nowUtc.AddDays(-2)));

        // 9. UNKNOWN (No projection at all)
        var order9 = Guid.NewGuid();
        var item9 = Guid.NewGuid();

        // 10. UNKNOWN (OrderId mismatch)
        var order10 = Guid.NewGuid();
        var item10 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), Guid.NewGuid(), item10, actorId, supplierId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), true, nowUtc.AddDays(-2)));

        // 11. UNKNOWN (Actor is neither renter nor supplier)
        var order11 = Guid.NewGuid();
        var item11 = Guid.NewGuid();
        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), order11, item11, Guid.NewGuid(), supplierId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), true, nowUtc.AddDays(-2)));

        await dbContext.SaveChangesAsync();

        var requestItems = new List<ReviewStatusItemRequest>
        {
            new(order1, item1),
            new(order2, item2),
            new(order3, item3),
            new(order4, item4),
            new(order5, item5),
            new(order6, item6),
            new(order7, item7),
            new(order8, item8),
            new(order9, item9),
            new(order10, item10),
            new(order11, item11)
        };

        var result = await service.GetBatchReviewStatusAsync(actorId, new BatchReviewStatusRequest(requestItems));

        Assert.True(result.IsSuccess);
        Assert.Equal(11, result.Value.Items.Count);

        // Verify request order preservation & exact states
        var res1 = result.Value.Items[0];
        Assert.Equal(order1, res1.OrderId);
        Assert.Equal(item1, res1.ItemId);
        Assert.Equal(ReviewStatusState.Reviewed, res1.ReviewStatus.State);
        Assert.Equal(review1.Id, res1.ReviewStatus.ReviewId);
        Assert.NotNull(res1.ReviewStatus.DeadlineAt);

        var res2 = result.Value.Items[1];
        Assert.Equal(ReviewStatusState.Reviewed, res2.ReviewStatus.State);
        Assert.Equal(review2.Id, res2.ReviewStatus.ReviewId);
        Assert.NotNull(res2.ReviewStatus.DeadlineAt);

        var res3 = result.Value.Items[2];
        Assert.Equal(ReviewStatusState.Reviewed, res3.ReviewStatus.State);
        Assert.Equal(review3.Id, res3.ReviewStatus.ReviewId);
        Assert.NotNull(res3.ReviewStatus.DeadlineAt);

        var res4 = result.Value.Items[3];
        Assert.Equal(ReviewStatusState.Reviewed, res4.ReviewStatus.State);
        Assert.Equal(review4.Id, res4.ReviewStatus.ReviewId);
        Assert.Null(res4.ReviewStatus.DeadlineAt); // No projection -> null deadline

        var res5 = result.Value.Items[4];
        Assert.Equal(ReviewStatusState.Available, res5.ReviewStatus.State);
        Assert.Null(res5.ReviewStatus.ReviewId);
        Assert.NotNull(res5.ReviewStatus.DeadlineAt);

        var res6 = result.Value.Items[5];
        Assert.Equal(ReviewStatusState.Expired, res6.ReviewStatus.State);
        Assert.Null(res6.ReviewStatus.ReviewId);
        Assert.NotNull(res6.ReviewStatus.DeadlineAt);

        var res7 = result.Value.Items[6];
        Assert.Equal(ReviewStatusState.NotEligible, res7.ReviewStatus.State);
        Assert.Null(res7.ReviewStatus.ReviewId);
        Assert.Null(res7.ReviewStatus.DeadlineAt);

        var res8 = result.Value.Items[7];
        Assert.Equal(ReviewStatusState.NotEligible, res8.ReviewStatus.State);
        Assert.Null(res8.ReviewStatus.ReviewId);
        Assert.Null(res8.ReviewStatus.DeadlineAt);

        var res9 = result.Value.Items[8];
        Assert.Equal(ReviewStatusState.Unknown, res9.ReviewStatus.State);
        Assert.Null(res9.ReviewStatus.ReviewId);
        Assert.Null(res9.ReviewStatus.DeadlineAt);

        var res10 = result.Value.Items[9];
        Assert.Equal(ReviewStatusState.Unknown, res10.ReviewStatus.State);
        Assert.Null(res10.ReviewStatus.ReviewId);
        Assert.Null(res10.ReviewStatus.DeadlineAt);

        var res11 = result.Value.Items[10];
        Assert.Equal(ReviewStatusState.Unknown, res11.ReviewStatus.State);
        Assert.Null(res11.ReviewStatus.ReviewId);
        Assert.Null(res11.ReviewStatus.DeadlineAt);
    }

    [Fact]
    public async Task GetBatchReviewStatusAsync_TwoWayEvaluation_EvaluatesIndependentlyPerActor()
    {
        using var dbContext = CreateDbContext();
        var (service, repository, timeProvider) = CreateService(dbContext);
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var nowUtc = timeProvider.GetUtcNow();

        await dbContext.ReviewProjections.AddAsync(new ReviewProjection(
            Guid.NewGuid(), orderId, itemId, renterId, supplierId, listingId,
            nowUtc.AddDays(-2), nowUtc.AddDays(28), true, nowUtc.AddDays(-2)));

        // Renter writes review
        var renterReview = Review.Create(
            Guid.NewGuid(), orderId, itemId, ReviewDirection.renter_to_supplier_listing,
            renterId, supplierId, listingId, 5, "Renter to supplier review", nowUtc.AddDays(-1), nowUtc.AddDays(6));
        await repository.AddAsync(renterReview);
        await dbContext.SaveChangesAsync();

        var batchReq = new BatchReviewStatusRequest([new ReviewStatusItemRequest(orderId, itemId)]);

        // 1. Renter queries: Should be REVIEWED
        var renterRes = await service.GetBatchReviewStatusAsync(renterId, batchReq);
        Assert.True(renterRes.IsSuccess);
        var renterItem = renterRes.Value.Items[0];
        Assert.Equal(ReviewStatusState.Reviewed, renterItem.ReviewStatus.State);
        Assert.Equal(renterReview.Id, renterItem.ReviewStatus.ReviewId);

        // 2. Supplier queries: Has not reviewed yet -> Should be AVAILABLE
        var supplierRes = await service.GetBatchReviewStatusAsync(supplierId, batchReq);
        Assert.True(supplierRes.IsSuccess);
        var supplierItem = supplierRes.Value.Items[0];
        Assert.Equal(ReviewStatusState.Available, supplierItem.ReviewStatus.State);
        Assert.Null(supplierItem.ReviewStatus.ReviewId);
        Assert.NotNull(supplierItem.ReviewStatus.DeadlineAt);

        // 3. Now Supplier also writes review
        var supplierReview = Review.Create(
            Guid.NewGuid(), orderId, itemId, ReviewDirection.supplier_to_renter,
            supplierId, renterId, listingId, 5, "Supplier to renter review", nowUtc, nowUtc.AddDays(7));
        await repository.AddAsync(supplierReview);
        await dbContext.SaveChangesAsync();

        // 4. Supplier queries again: Now Should be REVIEWED with Supplier's reviewId
        var supplierRes2 = await service.GetBatchReviewStatusAsync(supplierId, batchReq);
        Assert.True(supplierRes2.IsSuccess);
        var supplierItem2 = supplierRes2.Value.Items[0];
        Assert.Equal(ReviewStatusState.Reviewed, supplierItem2.ReviewStatus.State);
        Assert.Equal(supplierReview.Id, supplierItem2.ReviewStatus.ReviewId);
    }

    #endregion
}
