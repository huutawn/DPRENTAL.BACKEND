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

    private static (Application.Services.ReviewService Service, ReviewRepository Repository, TestTimeProvider TimeProvider) CreateService(ReviewServiceDbContext dbContext)
    {
        var repository = new ReviewRepository(dbContext);
        var idempotencyRepository = new IdempotencyRepository(dbContext);
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        var idempotencyService = new IdempotencyService(idempotencyRepository);
        var fakeRentalClient = new TestRentalServiceClient();

        var service = new Application.Services.ReviewService(
            dbContext,
            repository,
            idempotencyService,
            fakeRentalClient,
            timeProvider,
            NullLogger<Application.Services.ReviewService>.Instance);

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
    public async Task GetMyReviewsAsync_StringOverload_ValidatesActorProperly()
    {
        using var dbContext = CreateDbContext();
        var (service, _, _) = CreateService(dbContext);

        // Null string
        var nullResult = await service.GetMyReviewsAsync((string?)null, 1, 20);
        Assert.True(nullResult.IsFailure);
        Assert.Equal("UNAUTHORIZED", nullResult.Error.Code);

        // Invalid GUID string
        var invalidResult = await service.GetMyReviewsAsync("not-a-guid", 1, 20);
        Assert.True(invalidResult.IsFailure);
        Assert.Equal("UNAUTHORIZED", invalidResult.Error.Code);

        // Service token string
        var svcResult = await service.GetMyReviewsAsync("svc", 1, 20);
        Assert.True(svcResult.IsFailure);
        Assert.Equal("FORBIDDEN_SERVICE_TOKEN", svcResult.Error.Code);

        // Valid GUID string
        var validActorId = Guid.NewGuid();
        var validResult = await service.GetMyReviewsAsync(validActorId.ToString(), 1, 20);
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

}
