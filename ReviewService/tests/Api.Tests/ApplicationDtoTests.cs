using System.Globalization;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

public sealed class ApplicationDtoTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void CreateReviewRequest_SerializationAndProperties_Valid()
    {
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        CreateReviewRequest request = new(orderId, itemId, 5, "Bình luận tốt");

        Assert.Equal(orderId, request.OrderId);
        Assert.Equal(itemId, request.ItemId);
        Assert.Equal(5, request.Rating);
        Assert.Equal("Bình luận tốt", request.Comment);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        CreateReviewRequest? deserialized = JsonSerializer.Deserialize<CreateReviewRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(request.OrderId, deserialized.OrderId);
        Assert.Equal(request.ItemId, deserialized.ItemId);
        Assert.Equal(request.Rating, deserialized.Rating);
        Assert.Equal(request.Comment, deserialized.Comment);

        // Parameterless constructor
        CreateReviewRequest empty = new();
        Assert.Equal(Guid.Empty, empty.OrderId);
        Assert.Equal(Guid.Empty, empty.ItemId);
        Assert.Equal(0, empty.Rating);
        Assert.Null(empty.Comment);
    }

    [Fact]
    public void CreateReviewRequest_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
          "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
          "rating": 5,
          "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!"
        }
        """;

        CreateReviewRequest? dto = JsonSerializer.Deserialize<CreateReviewRequest>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Parse("4a713833-2882-4df3-a185-1bf035fe85d1"), dto.OrderId);
        Assert.Equal(Guid.Parse("b8f0474c-4da6-466d-a2dc-bf61dd3147f7"), dto.ItemId);
        Assert.Equal(5, dto.Rating);
        Assert.Equal("Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!", dto.Comment);
    }

    [Fact]
    public void UpdateReviewRequest_SerializationAndProperties_Valid()
    {
        UpdateReviewRequest request = new(4, "Cập nhật đánh giá");

        Assert.Equal(4, request.Rating);
        Assert.Equal("Cập nhật đánh giá", request.Comment);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        UpdateReviewRequest? deserialized = JsonSerializer.Deserialize<UpdateReviewRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(request.Rating, deserialized.Rating);
        Assert.Equal(request.Comment, deserialized.Comment);

        UpdateReviewRequest empty = new();
        Assert.Equal(0, empty.Rating);
        Assert.Null(empty.Comment);
    }

    [Fact]
    public void UpdateReviewRequest_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "rating": 4,
          "comment": "Cập nhật lại: máy quay rất tốt nhưng pin hơi chai nhẹ sau thời gian dài sử dụng."
        }
        """;

        UpdateReviewRequest? dto = JsonSerializer.Deserialize<UpdateReviewRequest>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(4, dto.Rating);
        Assert.Equal("Cập nhật lại: máy quay rất tốt nhưng pin hơi chai nhẹ sau thời gian dài sử dụng.", dto.Comment);
    }

    [Fact]
    public void WithdrawReviewRequest_SerializationAndProperties_Valid()
    {
        WithdrawReviewRequest request = new("Hiểu lầm đã được giải quyết");

        Assert.Equal("Hiểu lầm đã được giải quyết", request.Reason);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        WithdrawReviewRequest? deserialized = JsonSerializer.Deserialize<WithdrawReviewRequest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(request.Reason, deserialized.Reason);

        WithdrawReviewRequest empty = new();
        Assert.Null(empty.Reason);
    }

    [Fact]
    public void WithdrawReviewRequest_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "reason": "Tôi muốn rút lại đánh giá do đã giải quyết hiểu lầm với đối tác."
        }
        """;

        WithdrawReviewRequest? dto = JsonSerializer.Deserialize<WithdrawReviewRequest>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal("Tôi muốn rút lại đánh giá do đã giải quyết hiểu lầm với đối tác.", dto.Reason);
    }

    [Fact]
    public void ReviewDetailResponse_FromEntity_MapsAllFieldsCorrectly()
    {
        Guid reviewId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        Guid authorId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        Guid listingId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddDays(-2);
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset canEditUntil = createdAt.AddDays(7);

        Review review = new(
            id: reviewId,
            orderId: orderId,
            itemId: itemId,
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: listingId,
            rating: 5,
            comment: "Dịch vụ xuất sắc",
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 2,
            createdAt: createdAt,
            updatedAt: updatedAt,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: canEditUntil);

        ReviewDetailResponse response = ReviewDetailResponse.FromEntity(review);

        Assert.Equal(reviewId, response.ReviewId);
        Assert.Equal(orderId, response.OrderId);
        Assert.Equal(itemId, response.ItemId);
        Assert.Equal("renter_to_supplier_listing", response.Direction);
        Assert.Equal(authorId, response.AuthorId);
        Assert.Equal(subjectId, response.SubjectId);
        Assert.Equal(listingId, response.EquipmentListingId);
        Assert.Equal(5, response.Rating);
        Assert.Equal("Dịch vụ xuất sắc", response.Comment);
        Assert.Equal("Active", response.AuthorState);
        Assert.Equal("Visible", response.ModerationState);
        Assert.Equal(2, response.Version);
        Assert.Equal(createdAt, response.CreatedAt);
        Assert.Equal(updatedAt, response.UpdatedAt);
        Assert.Null(response.WithdrawnAt);
        Assert.Equal(canEditUntil, response.CanEditUntil);
    }

    [Fact]
    public void ReviewDetailResponse_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
          "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
          "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
          "direction": "renter_to_supplier_listing",
          "authorId": "11111111-1111-1111-1111-111111111111",
          "subjectId": "22222222-2222-2222-2222-222222222222",
          "equipmentListingId": "33333333-3333-3333-3333-333333333333",
          "rating": 5,
          "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!",
          "authorState": "Active",
          "moderationState": "Visible",
          "version": 1,
          "createdAt": "2026-11-02T08:30:00.000Z",
          "updatedAt": null,
          "withdrawnAt": null,
          "canEditUntil": "2026-11-09T08:30:00.000Z"
        }
        """;

        ReviewDetailResponse? dto = JsonSerializer.Deserialize<ReviewDetailResponse>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Parse("8f731888-eb87-43cf-8a21-72946c87e6aa"), dto.ReviewId);
        Assert.Equal(Guid.Parse("4a713833-2882-4df3-a185-1bf035fe85d1"), dto.OrderId);
        Assert.Equal(Guid.Parse("b8f0474c-4da6-466d-a2dc-bf61dd3147f7"), dto.ItemId);
        Assert.Equal("renter_to_supplier_listing", dto.Direction);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), dto.AuthorId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), dto.SubjectId);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), dto.EquipmentListingId);
        Assert.Equal(5, dto.Rating);
        Assert.Equal("Active", dto.AuthorState);
        Assert.Equal("Visible", dto.ModerationState);
        Assert.Equal(1, dto.Version);
        Assert.Null(dto.UpdatedAt);
        Assert.Null(dto.WithdrawnAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-09T08:30:00.000Z", CultureInfo.InvariantCulture), dto.CanEditUntil);
    }

    [Fact]
    public void RentalItemReviewSnapshotDto_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "orderId": "4a713833-2882-4df3-a185-1bf035fe85d1",
          "itemId": "b8f0474c-4da6-466d-a2dc-bf61dd3147f7",
          "renterId": "11111111-1111-1111-1111-111111111111",
          "supplierId": "22222222-2222-2222-2222-222222222222",
          "equipmentListingId": "33333333-3333-3333-3333-333333333333",
          "status": "Completed",
          "completedAt": "2026-10-15T10:00:00.000Z",
          "orderVersion": 5
        }
        """;

        RentalItemReviewSnapshotDto? dto = JsonSerializer.Deserialize<RentalItemReviewSnapshotDto>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(Guid.Parse("4a713833-2882-4df3-a185-1bf035fe85d1"), dto.OrderId);
        Assert.Equal(Guid.Parse("b8f0474c-4da6-466d-a2dc-bf61dd3147f7"), dto.ItemId);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), dto.RenterId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), dto.SupplierId);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), dto.EquipmentListingId);
        Assert.Equal("Completed", dto.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T10:00:00.000Z", CultureInfo.InvariantCulture), dto.CompletedAt);
        Assert.Equal(5, dto.OrderVersion);
    }

    [Fact]
    public void PublicReviewItemResponse_SerializationAndProperties_Valid()
    {
        Guid reviewId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Default AuthorMaskedName is "Khách hàng"
        PublicReviewItemResponse itemDefault = new(reviewId, 5, "Rất tốt", now, null);
        Assert.Equal("Khách hàng", itemDefault.AuthorMaskedName);
        Assert.Equal(reviewId, itemDefault.ReviewId);
        Assert.Equal(5, itemDefault.Rating);
        Assert.Equal("Rất tốt", itemDefault.Comment);
        Assert.Equal(now, itemDefault.CreatedAt);
        Assert.Null(itemDefault.UpdatedAt);

        // Custom AuthorMaskedName
        PublicReviewItemResponse itemCustom = new(reviewId, 4, "Tốt", now, now.AddHours(1), "Người dùng ẩn danh");
        Assert.Equal("Người dùng ẩn danh", itemCustom.AuthorMaskedName);

        // Parameterless constructor defaults
        PublicReviewItemResponse itemEmpty = new();
        Assert.Equal(Guid.Empty, itemEmpty.ReviewId);
        Assert.Equal(0, itemEmpty.Rating);
        Assert.Null(itemEmpty.Comment);
        Assert.Null(itemEmpty.UpdatedAt);
        Assert.Equal("Khách hàng", itemEmpty.AuthorMaskedName);

        // FromEntity
        Review review = Review.Create(
            id: reviewId,
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: ReviewDirection.renter_to_supplier_listing,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 5,
            comment: "Từ entity",
            createdAt: now,
            canEditUntil: now.AddDays(7));

        PublicReviewItemResponse fromEntity = PublicReviewItemResponse.FromEntity(review);
        Assert.Equal(reviewId, fromEntity.ReviewId);
        Assert.Equal(5, fromEntity.Rating);
        Assert.Equal("Từ entity", fromEntity.Comment);
        Assert.Equal("Khách hàng", fromEntity.AuthorMaskedName);

        // Round-trip serialization
        string json = JsonSerializer.Serialize(itemDefault, JsonOptions);
        PublicReviewItemResponse? deserialized = JsonSerializer.Deserialize<PublicReviewItemResponse>(json, JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(itemDefault.ReviewId, deserialized.ReviewId);
        Assert.Equal(itemDefault.Rating, deserialized.Rating);
        Assert.Equal(itemDefault.Comment, deserialized.Comment);
        Assert.Equal(itemDefault.CreatedAt, deserialized.CreatedAt);
        Assert.Equal(itemDefault.AuthorMaskedName, deserialized.AuthorMaskedName);
    }

    [Fact]
    public void PublicListingReviewsResponse_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "items": [
            {
              "reviewId": "8f731888-eb87-43cf-8a21-72946c87e6aa",
              "rating": 5,
              "comment": "Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!",
              "createdAt": "2026-11-02T08:30:00.000Z",
              "updatedAt": null,
              "authorMaskedName": "Khách hàng"
            }
          ],
          "page": 1,
          "pageSize": 20,
          "totalCount": 1,
          "totalPages": 1
        }
        """;

        PagedResult<PublicReviewItemResponse>? dto =
            JsonSerializer.Deserialize<PagedResult<PublicReviewItemResponse>>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal(1, dto.Page);
        Assert.Equal(20, dto.PageSize);
        Assert.Equal(1, dto.TotalCount);
        Assert.Equal(1, dto.TotalPages);
        Assert.Single(dto.Items);

        PublicReviewItemResponse item = dto.Items[0];
        Assert.Equal(Guid.Parse("8f731888-eb87-43cf-8a21-72946c87e6aa"), item.ReviewId);
        Assert.Equal(5, item.Rating);
        Assert.Equal("Thiết bị máy quay Sony FX3 hoạt động rất tốt, phụ kiện đi kèm đầy đủ và sạch sẽ!", item.Comment);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T08:30:00.000Z", CultureInfo.InvariantCulture), item.CreatedAt);
        Assert.Null(item.UpdatedAt);
        Assert.Equal("Khách hàng", item.AuthorMaskedName);

        // Round-trip serialization parity
        string serialized = JsonSerializer.Serialize(dto, JsonOptions);
        using var expectedDoc = JsonDocument.Parse(fixtureJson);
        using var actualDoc = JsonDocument.Parse(serialized);

        Assert.Equal(expectedDoc.RootElement.GetProperty("page").GetInt32(), actualDoc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(expectedDoc.RootElement.GetProperty("pageSize").GetInt32(), actualDoc.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(expectedDoc.RootElement.GetProperty("totalCount").GetInt32(), actualDoc.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(expectedDoc.RootElement.GetProperty("totalPages").GetInt32(), actualDoc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(
            expectedDoc.RootElement.GetProperty("items")[0].GetProperty("authorMaskedName").GetString(),
            actualDoc.RootElement.GetProperty("items")[0].GetProperty("authorMaskedName").GetString());

        // Disk fixture verification if available
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "fixtures", "valid", "public-listing-reviews-response.json");
        if (File.Exists(fixturePath))
        {
            string diskContent = File.ReadAllText(fixturePath);
            var fromDisk = JsonSerializer.Deserialize<PagedResult<PublicReviewItemResponse>>(diskContent, JsonOptions);
            Assert.NotNull(fromDisk);
            Assert.Equal(dto.TotalCount, fromDisk.TotalCount);
            Assert.Equal(dto.Items[0].ReviewId, fromDisk.Items[0].ReviewId);
        }
    }

    [Fact]
    public void ReviewSummaryResponse_SerializationAndProperties_Valid()
    {
        Guid targetId = Guid.NewGuid();
        var dist = new Dictionary<string, int>
        {
            ["5"] = 10,
            ["4"] = 2,
            ["3"] = 1,
            ["2"] = 0,
            ["1"] = 0
        };

        ReviewSummaryResponse summary = new("listing", targetId, 4.7, 13, dist);
        Assert.Equal("listing", summary.TargetType);
        Assert.Equal(targetId, summary.TargetId);
        Assert.Equal(4.7, summary.AverageRating);
        Assert.Equal(13, summary.ReviewCount);
        Assert.Equal(10, summary.RatingDistribution["5"]);

        // Empty helper
        ReviewSummaryResponse empty = ReviewSummaryResponse.Empty("supplier", targetId);
        Assert.Equal("supplier", empty.TargetType);
        Assert.Equal(targetId, empty.TargetId);
        Assert.Null(empty.AverageRating);
        Assert.Equal(0, empty.ReviewCount);
        Assert.Equal(0, empty.RatingDistribution["5"]);
        Assert.Equal(0, empty.RatingDistribution["1"]);

        // Parameterless constructor
        ReviewSummaryResponse defaultCtor = new();
        Assert.Equal(string.Empty, defaultCtor.TargetType);
        Assert.Equal(Guid.Empty, defaultCtor.TargetId);
        Assert.Null(defaultCtor.AverageRating);
        Assert.Equal(0, defaultCtor.ReviewCount);
        Assert.Equal(5, defaultCtor.RatingDistribution.Count);

        // Roundtrip serialization
        string json = JsonSerializer.Serialize(summary, JsonOptions);
        ReviewSummaryResponse? deserialized = JsonSerializer.Deserialize<ReviewSummaryResponse>(json, JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(summary.TargetType, deserialized.TargetType);
        Assert.Equal(summary.TargetId, deserialized.TargetId);
        Assert.Equal(summary.AverageRating, deserialized.AverageRating);
        Assert.Equal(summary.ReviewCount, deserialized.ReviewCount);
        Assert.Equal(summary.RatingDistribution["5"], deserialized.RatingDistribution["5"]);
    }

    [Fact]
    public void ReviewSummaryResponse_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "targetType": "listing",
          "targetId": "33333333-3333-3333-3333-333333333333",
          "averageRating": 4.8,
          "reviewCount": 15,
          "ratingDistribution": {
            "5": 12,
            "4": 3,
            "3": 0,
            "2": 0,
            "1": 0
          }
        }
        """;

        ReviewSummaryResponse? dto = JsonSerializer.Deserialize<ReviewSummaryResponse>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal("listing", dto.TargetType);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), dto.TargetId);
        Assert.Equal(4.8, dto.AverageRating);
        Assert.Equal(15, dto.ReviewCount);
        Assert.Equal(5, dto.RatingDistribution.Count);
        Assert.Equal(12, dto.RatingDistribution["5"]);
        Assert.Equal(3, dto.RatingDistribution["4"]);
        Assert.Equal(0, dto.RatingDistribution["3"]);
        Assert.Equal(0, dto.RatingDistribution["2"]);
        Assert.Equal(0, dto.RatingDistribution["1"]);

        // Round-trip serialization parity
        string serialized = JsonSerializer.Serialize(dto, JsonOptions);
        using var expectedDoc = JsonDocument.Parse(fixtureJson);
        using var actualDoc = JsonDocument.Parse(serialized);

        Assert.Equal(expectedDoc.RootElement.GetProperty("targetType").GetString(), actualDoc.RootElement.GetProperty("targetType").GetString());
        Assert.Equal(expectedDoc.RootElement.GetProperty("averageRating").GetDouble(), actualDoc.RootElement.GetProperty("averageRating").GetDouble());
        Assert.Equal(expectedDoc.RootElement.GetProperty("reviewCount").GetInt32(), actualDoc.RootElement.GetProperty("reviewCount").GetInt32());
        Assert.Equal(
            expectedDoc.RootElement.GetProperty("ratingDistribution").GetProperty("5").GetInt32(),
            actualDoc.RootElement.GetProperty("ratingDistribution").GetProperty("5").GetInt32());

        // Disk fixture verification if available
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "fixtures", "valid", "review-summary-response.json");
        if (File.Exists(fixturePath))
        {
            string diskContent = File.ReadAllText(fixturePath);
            var fromDisk = JsonSerializer.Deserialize<ReviewSummaryResponse>(diskContent, JsonOptions);
            Assert.NotNull(fromDisk);
            Assert.Equal(dto.TargetId, fromDisk.TargetId);
            Assert.Equal(dto.AverageRating, fromDisk.AverageRating);
        }
    }

    [Fact]
    public void ReviewSummaryEmptyResponse_MatchesContractFixture()
    {
        string fixtureJson = """
        {
          "targetType": "listing",
          "targetId": "44444444-4444-4444-4444-444444444444",
          "averageRating": null,
          "reviewCount": 0,
          "ratingDistribution": {
            "5": 0,
            "4": 0,
            "3": 0,
            "2": 0,
            "1": 0
          }
        }
        """;

        ReviewSummaryResponse? dto = JsonSerializer.Deserialize<ReviewSummaryResponse>(fixtureJson, JsonOptions);

        Assert.NotNull(dto);
        Assert.Equal("listing", dto.TargetType);
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), dto.TargetId);
        Assert.Null(dto.AverageRating);
        Assert.Equal(0, dto.ReviewCount);
        Assert.Equal(5, dto.RatingDistribution.Count);
        Assert.Equal(0, dto.RatingDistribution["5"]);
        Assert.Equal(0, dto.RatingDistribution["4"]);
        Assert.Equal(0, dto.RatingDistribution["3"]);
        Assert.Equal(0, dto.RatingDistribution["2"]);
        Assert.Equal(0, dto.RatingDistribution["1"]);

        // Round-trip serialization parity
        string serialized = JsonSerializer.Serialize(dto, JsonOptions);
        using var expectedDoc = JsonDocument.Parse(fixtureJson);
        using var actualDoc = JsonDocument.Parse(serialized);

        Assert.Equal(JsonValueKind.Null, actualDoc.RootElement.GetProperty("averageRating").ValueKind);
        Assert.Equal(0, actualDoc.RootElement.GetProperty("reviewCount").GetInt32());

        // Disk fixture verification if available
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "fixtures", "valid", "review-summary-empty-response.json");
        if (File.Exists(fixturePath))
        {
            string diskContent = File.ReadAllText(fixturePath);
            var fromDisk = JsonSerializer.Deserialize<ReviewSummaryResponse>(diskContent, JsonOptions);
            Assert.NotNull(fromDisk);
            Assert.Equal(dto.TargetId, fromDisk.TargetId);
            Assert.Null(fromDisk.AverageRating);
            Assert.Equal(0, fromDisk.ReviewCount);
        }
    }

    [Fact]
    public void PagedResult_Create_CalculatesTotalPagesProperly()
    {
        var emptyResult = PagedResult<int>.Create([], 1, 20, 0);
        Assert.Equal(1, emptyResult.Page);
        Assert.Equal(20, emptyResult.PageSize);
        Assert.Equal(0, emptyResult.TotalCount);
        Assert.Equal(0, emptyResult.TotalPages);

        var singleResult = PagedResult<int>.Create([1], 1, 20, 1);
        Assert.Equal(1, singleResult.TotalPages);

        var exactPage = PagedResult<int>.Create([1, 2], 1, 2, 2);
        Assert.Equal(1, exactPage.TotalPages);

        var multiplePages = PagedResult<int>.Create([1], 1, 20, 21);
        Assert.Equal(2, multiplePages.TotalPages);

        // Invalid bounds sanitized
        var sanitized = PagedResult<int>.Create([1], -5, -10, 5);
        Assert.Equal(1, sanitized.Page);
        Assert.Equal(1, sanitized.PageSize);
        Assert.Equal(5, sanitized.TotalPages);
    }
}
