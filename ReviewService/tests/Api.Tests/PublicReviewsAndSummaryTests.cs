using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử chấp nhận cho các endpoint công khai (Public Listing, Supplier, Summary) và Đánh giá cá nhân (/my).
/// </summary>
public sealed class PublicReviewsAndSummaryTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public PublicReviewsAndSummaryTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task GetListingReviews_ReturnsOnlyActiveAndVisibleReviews()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        // Tạo 1 review hợp lệ
        var orderId1 = Guid.NewGuid();
        var itemId1 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId1, itemId1, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-2), 1));

        var createReq = new CreateReviewRequest(orderId1, itemId1, 5, "Máy ảnh hoạt động hoàn hảo!");
        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews", createReq, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var postRes = await _client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);

        // Gọi GET listing reviews
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/listings/{listingId}");
        using var getRes = await _client.SendAsync(getReq);

        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var content = await getRes.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<PublicReviewItemResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.True(paged.TotalCount >= 1);
        var item = Assert.Single(paged.Items, i => i.Comment == "Máy ảnh hoạt động hoàn hảo!");
        Assert.Equal(5, item.Rating);
        Assert.Equal("Khách hàng", item.AuthorMaskedName);
    }

    [Fact]
    public async Task GetListingReviews_EmptyTarget_Returns200WithEmptyListNot404()
    {
        var nonExistentListingId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/listings/{nonExistentListingId}");
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<PublicReviewItemResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.Equal(0, paged.TotalCount);
        Assert.Empty(paged.Items);
    }

    [Fact]
    public async Task GetSupplierReviews_EmptyTarget_Returns200WithEmptyListNot404()
    {
        var nonExistentSupplierId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/suppliers/{nonExistentSupplierId}");
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<PublicReviewItemResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.Equal(0, paged.TotalCount);
        Assert.Empty(paged.Items);
    }

    [Fact]
    public async Task GetSummary_ReturnsCorrectAverageAndFullDistribution()
    {
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        // Tạo 2 review: 1 cái rating 5, 1 cái rating 4
        var r1 = Guid.NewGuid();
        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            order1, item1, r1, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-3), 1));
        using var post1 = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(order1, item1, 5, "Tuyệt vời 5 sao!"), GenerateJwt(r1), Guid.NewGuid().ToString("N"));
        await _client.SendAsync(post1);

        var r2 = Guid.NewGuid();
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            order2, item2, r2, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-3), 1));
        using var post2 = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(order2, item2, 4, "Rất tốt 4 sao!"), GenerateJwt(r2), Guid.NewGuid().ToString("N"));
        await _client.SendAsync(post2);

        // Gọi summary cho listing
        using var sumReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/summary?targetType=listing&targetId={listingId}");
        using var sumRes = await _client.SendAsync(sumReq);

        Assert.Equal(HttpStatusCode.OK, sumRes.StatusCode);
        var content = await sumRes.Content.ReadAsStringAsync();
        var summary = JsonSerializer.Deserialize<ReviewSummaryResponse>(content, JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal("listing", summary.TargetType);
        Assert.Equal(listingId, summary.TargetId);
        Assert.Equal(2, summary.ReviewCount);
        Assert.Equal(4.5, summary.AverageRating);
        Assert.Equal(5, summary.RatingDistribution.Count);
        Assert.Equal(1, summary.RatingDistribution["5"]);
        Assert.Equal(1, summary.RatingDistribution["4"]);
        Assert.Equal(0, summary.RatingDistribution["3"]);
        Assert.Equal(0, summary.RatingDistribution["2"]);
        Assert.Equal(0, summary.RatingDistribution["1"]);
    }

    [Fact]
    public async Task GetSummary_EmptyTarget_ReturnsAverageNullAndCountZero()
    {
        var targetId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/summary?targetType=listing&targetId={targetId}");
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var summary = JsonSerializer.Deserialize<ReviewSummaryResponse>(content, JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal("listing", summary.TargetType);
        Assert.Equal(targetId, summary.TargetId);
        Assert.Equal(0, summary.ReviewCount);
        Assert.Null(summary.AverageRating);
        Assert.Equal(5, summary.RatingDistribution.Count);
        Assert.Equal(0, summary.RatingDistribution["5"]);
        Assert.Equal(0, summary.RatingDistribution["1"]);
    }

    [Fact]
    public async Task GetSummary_InvalidTargetType_Returns400BadRequest()
    {
        var targetId = Guid.NewGuid();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/summary?targetType=invalid_type&targetId={targetId}");
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetMyReviews_Anonymous_Returns401Unauthorized()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task GetMyReviews_Authenticated_ReturnsReviewsWrittenByCaller()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 4, "Review của tôi"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        await _client.SendAsync(postReq);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(renterId));
        using var getRes = await _client.SendAsync(getReq);

        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var content = await getRes.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<ReviewDetailResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.Contains(paged.Items, i => i.AuthorId == renterId && i.ItemId == itemId);
    }
    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body, string? jwt, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(jwt))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static string GenerateJwt(Guid userId, string? userType = "cus", string? role = null)
    {
        var payload = new Dictionary<string, object> { ["sub"] = userId.ToString() };
        if (!string.IsNullOrEmpty(userType)) payload["u"] = userType;
        if (!string.IsNullOrEmpty(role)) payload["r"] = role;

        string json = JsonSerializer.Serialize(payload);
        string base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"eyJhbGciOiJub25lIn0.{base64Payload}.testsignature";
    }
}
