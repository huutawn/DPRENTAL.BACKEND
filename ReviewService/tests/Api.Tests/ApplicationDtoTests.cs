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
}
