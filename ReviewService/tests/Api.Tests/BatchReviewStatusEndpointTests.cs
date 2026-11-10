using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử biên và tích hợp HTTP cho API kiểm tra trạng thái đánh giá theo lô (POST /api/v1/reviews/status/batch).
/// </summary>
public sealed class BatchReviewStatusEndpointTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public BatchReviewStatusEndpointTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        object? body = null,
        string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task BatchStatus_WithoutToken_Returns401Unauthorized()
    {
        var requestBody = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid())
        ]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_ServiceToken_Returns403Forbidden()
    {
        var serviceId = Guid.NewGuid();
        var serviceToken = _fixture.GenerateJwt(serviceId, userType: "svc");

        var requestBody = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid())
        ]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, serviceToken);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_EmptyItems_Returns400BadRequest()
    {
        var userId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(userId);

        var requestBody = new BatchReviewStatusRequest([]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_ExceedingLimit_Returns400BadRequest()
    {
        var userId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(userId);

        var items = Enumerable.Range(1, 101)
            .Select(_ => new ReviewStatusItemRequest(Guid.NewGuid(), Guid.NewGuid()))
            .ToList();

        var requestBody = new BatchReviewStatusRequest(items);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_DuplicatePair_Returns400BadRequest()
    {
        var userId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(userId);
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var requestBody = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(orderId, itemId),
            new ReviewStatusItemRequest(orderId, itemId)
        ]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_EmptyGuidInPair_Returns400BadRequest()
    {
        var userId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(userId);

        var requestBody = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(Guid.Empty, Guid.NewGuid())
        ]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchStatus_EndToEnd_EvaluatesAllStatesAndMaintainsOrder()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var now = _fixture.CurrentTime;

        // 1. Item reviewed by renter
        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        var reviewId1 = Guid.NewGuid();

        // 2. Item available for review
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();

        // 3. Item expired
        var order3 = Guid.NewGuid();
        var item3 = Guid.NewGuid();

        // 4. Item not eligible
        var order4 = Guid.NewGuid();
        var item4 = Guid.NewGuid();

        // 5. Item unknown (no projection)
        var order5 = Guid.NewGuid();
        var item5 = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var projectionRepo = scope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
            var reviewRepo = scope.ServiceProvider.GetRequiredService<IReviewRepository>();

            // Setup Projections
            await projectionRepo.UpsertAsync(new ReviewProjection(
                Guid.NewGuid(), order1, item1, renterId, supplierId, listingId,
                now.AddDays(-5), now.AddDays(25), true, now.AddDays(-5)));

            await projectionRepo.UpsertAsync(new ReviewProjection(
                Guid.NewGuid(), order2, item2, renterId, supplierId, listingId,
                now.AddDays(-2), now.AddDays(28), true, now.AddDays(-2)));

            await projectionRepo.UpsertAsync(new ReviewProjection(
                Guid.NewGuid(), order3, item3, renterId, supplierId, listingId,
                now.AddDays(-35), now.AddDays(-5), true, now.AddDays(-35)));

            await projectionRepo.UpsertAsync(new ReviewProjection(
                Guid.NewGuid(), order4, item4, renterId, supplierId, listingId,
                now.AddDays(-2), now.AddDays(28), false, now.AddDays(-2)));

            // Setup Review for item 1 by renter
            var review1 = Review.Create(
                reviewId1, order1, item1, ReviewDirection.renter_to_supplier_listing,
                renterId, supplierId, listingId, 5, "Great experience", now.AddDays(-3), now.AddDays(4));
            await reviewRepo.AddAsync(review1);
        }

        var token = _fixture.GenerateJwt(renterId);
        var requestBody = new BatchReviewStatusRequest([
            new ReviewStatusItemRequest(order1, item1),
            new ReviewStatusItemRequest(order2, item2),
            new ReviewStatusItemRequest(order3, item3),
            new ReviewStatusItemRequest(order4, item4),
            new ReviewStatusItemRequest(order5, item5)
        ]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize<BatchReviewStatusResponse>(content, JsonOptions);

        Assert.NotNull(batchResponse);
        Assert.Equal(5, batchResponse.Items.Count);

        // Item 1: REVIEWED
        var i1 = batchResponse.Items[0];
        Assert.Equal(order1, i1.OrderId);
        Assert.Equal(item1, i1.ItemId);
        Assert.Equal(ReviewStatusState.Reviewed, i1.ReviewStatus.State);
        Assert.Equal(reviewId1, i1.ReviewStatus.ReviewId);
        Assert.NotNull(i1.ReviewStatus.DeadlineAt);

        // Item 2: AVAILABLE
        var i2 = batchResponse.Items[1];
        Assert.Equal(order2, i2.OrderId);
        Assert.Equal(item2, i2.ItemId);
        Assert.Equal(ReviewStatusState.Available, i2.ReviewStatus.State);
        Assert.Null(i2.ReviewStatus.ReviewId);
        Assert.NotNull(i2.ReviewStatus.DeadlineAt);

        // Item 3: EXPIRED
        var i3 = batchResponse.Items[2];
        Assert.Equal(order3, i3.OrderId);
        Assert.Equal(item3, i3.ItemId);
        Assert.Equal(ReviewStatusState.Expired, i3.ReviewStatus.State);
        Assert.Null(i3.ReviewStatus.ReviewId);
        Assert.NotNull(i3.ReviewStatus.DeadlineAt);

        // Item 4: NOT_ELIGIBLE
        var i4 = batchResponse.Items[3];
        Assert.Equal(order4, i4.OrderId);
        Assert.Equal(item4, i4.ItemId);
        Assert.Equal(ReviewStatusState.NotEligible, i4.ReviewStatus.State);
        Assert.Null(i4.ReviewStatus.ReviewId);
        Assert.Null(i4.ReviewStatus.DeadlineAt);

        // Item 5: UNKNOWN
        var i5 = batchResponse.Items[4];
        Assert.Equal(order5, i5.OrderId);
        Assert.Equal(item5, i5.ItemId);
        Assert.Equal(ReviewStatusState.Unknown, i5.ReviewStatus.State);
        Assert.Null(i5.ReviewStatus.ReviewId);
        Assert.Null(i5.ReviewStatus.DeadlineAt);
    }

    [Fact]
    public async Task BatchStatus_NonParticipant_ReturnsUnknownWithoutLeakingMetadata()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var nonParticipantId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var now = _fixture.CurrentTime;

        using (var scope = _fixture.Services.CreateScope())
        {
            var projectionRepo = scope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
            await projectionRepo.UpsertAsync(new ReviewProjection(
                Guid.NewGuid(), orderId, itemId, renterId, supplierId, listingId,
                now.AddDays(-2), now.AddDays(28), true, now.AddDays(-2)));
        }

        var nonParticipantToken = _fixture.GenerateJwt(nonParticipantId);
        var requestBody = new BatchReviewStatusRequest([new ReviewStatusItemRequest(orderId, itemId)]);

        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews/status/batch", requestBody, nonParticipantToken);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var batchResponse = JsonSerializer.Deserialize<BatchReviewStatusResponse>(content, JsonOptions);

        Assert.NotNull(batchResponse);
        var item = Assert.Single(batchResponse.Items);
        Assert.Equal(ReviewStatusState.Unknown, item.ReviewStatus.State);
        Assert.Null(item.ReviewStatus.ReviewId);
        Assert.Null(item.ReviewStatus.DeadlineAt);
    }
}
