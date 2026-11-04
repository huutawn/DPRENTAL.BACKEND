using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử chấp nhận toàn diện cho vòng đời Review và các biên HTTP / Idempotency / Concurrency.
/// </summary>
public sealed class ReviewLifecycleAndBoundaryTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public ReviewLifecycleAndBoundaryTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task Scenario01_CreateReview_Success_Returns201CreatedWithEtag1AndLocation()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("N");

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var requestBody = new CreateReviewRequest(orderId, itemId, 5, "Thiết bị máy quay Sony FX3 hoạt động rất tốt!");
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(renterId), key);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("\"1\"", response.Headers.ETag.Tag);
        Assert.False(response.Headers.ETag.IsWeak);

        var content = await response.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(content, JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal(orderId, detail.OrderId);
        Assert.Equal(itemId, detail.ItemId);
        Assert.Equal(5, detail.Rating);
        Assert.Equal(1, detail.Version);
        Assert.Equal("Active", detail.AuthorState);
        Assert.Equal("Visible", detail.ModerationState);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"/api/v1/reviews/{detail.ReviewId}", response.Headers.Location.OriginalString);
    }

    [Fact]
    public async Task Scenario02_CreateReview_IdempotencyReplay_ReturnsOriginal201WithIdenticalEtag()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("N");

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var requestBody = new CreateReviewRequest(orderId, itemId, 5, "Replay test review");
        using var firstRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(renterId), key);
        using var firstResponse = await _client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstContent = await firstResponse.Content.ReadAsStringAsync();
        var firstDetail = JsonSerializer.Deserialize<ReviewDetailResponse>(firstContent, JsonOptions);
        Assert.NotNull(firstDetail);

        using var secondRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(renterId), key);
        using var secondResponse = await _client.SendAsync(secondRequest);

        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.NotNull(secondResponse.Headers.ETag);
        Assert.Equal("\"1\"", secondResponse.Headers.ETag.Tag);
        Assert.Equal($"/api/v1/reviews/{firstDetail.ReviewId}", secondResponse.Headers.Location?.OriginalString);

        var secondContent = await secondResponse.Content.ReadAsStringAsync();
        var secondDetail = JsonSerializer.Deserialize<ReviewDetailResponse>(secondContent, JsonOptions);
        Assert.NotNull(secondDetail);
        Assert.Equal(firstDetail.ReviewId, secondDetail.ReviewId);
        Assert.Equal(firstDetail.Version, secondDetail.Version);
    }

    [Fact]
    public async Task Scenario03_CreateReview_IdempotencyKeyReuseDifferentBody_Returns409Conflict()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("N");

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var initialBody = new CreateReviewRequest(orderId, itemId, 5, "Initial comment");
        using var initialRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", initialBody, GenerateJwt(renterId), key);
        using var initialResponse = await _client.SendAsync(initialRequest);
        Assert.Equal(HttpStatusCode.Created, initialResponse.StatusCode);

        var differentBody = new CreateReviewRequest(orderId, itemId, 4, "Altered comment with different rating");
        using var conflictRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", differentBody, GenerateJwt(renterId), key);
        using var conflictResponse = await _client.SendAsync(conflictRequest);

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        var conflictContent = await conflictResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(conflictContent, "IDEMPOTENCY_KEY_REUSE_MISMATCH");
    }

    [Fact]
    public async Task Scenario04_CreateReview_DuplicateReviewOnSameItemAndDirection_Returns409Conflict()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var firstBody = new CreateReviewRequest(orderId, itemId, 5, "First submission");
        using var firstRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", firstBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var firstResponse = await _client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        var duplicateBody = new CreateReviewRequest(orderId, itemId, 4, "Duplicate submission with fresh key");
        using var duplicateRequest = CreateRequest(HttpMethod.Post, "/api/v1/reviews", duplicateBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var duplicateResponse = await _client.SendAsync(duplicateRequest);

        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var content = await duplicateResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(content, "REVIEW_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Scenario05_CreateReview_CreationWindowExpired_Returns422UnprocessableEntity()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-31)));

        var requestBody = new CreateReviewRequest(orderId, itemId, 5, "Expired window review");
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        AssertProblemTitle(content, "REVIEW_CREATION_WINDOW_EXPIRED");
    }

    [Fact]
    public async Task Scenario06_Mutation_MissingIfMatch_Returns428PreconditionRequired()
    {
        var review = await CreateActiveReviewAsync();

        var updateBody = new UpdateReviewRequest(4, "Update without If-Match");
        using var putRequest = CreateRequest(HttpMethod.Put, $"/api/v1/reviews/{review.ReviewId}", updateBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"));
        using var putResponse = await _client.SendAsync(putRequest);
        Assert.Equal((HttpStatusCode)428, putResponse.StatusCode);
        var putContent = await putResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(putContent, "PRECONDITION_REQUIRED");

        var withdrawBody = new WithdrawReviewRequest("Withdraw without If-Match");
        using var postWithdrawRequest = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{review.ReviewId}/withdraw", withdrawBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"));
        using var postWithdrawResponse = await _client.SendAsync(postWithdrawRequest);
        Assert.Equal((HttpStatusCode)428, postWithdrawResponse.StatusCode);
        var withdrawContent = await postWithdrawResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(withdrawContent, "PRECONDITION_REQUIRED");
    }

    [Fact]
    public async Task Scenario07_Mutation_StaleIfMatch_Returns412PreconditionFailed()
    {
        var review = await CreateActiveReviewAsync();

        var updateBody = new UpdateReviewRequest(4, "Stale update");
        using var putRequest = CreateRequest(HttpMethod.Put, $"/api/v1/reviews/{review.ReviewId}", updateBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"99\"");
        using var putResponse = await _client.SendAsync(putRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, putResponse.StatusCode);
        var putContent = await putResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(putContent, "ETAG_PRECONDITION_FAILED");

        var withdrawBody = new WithdrawReviewRequest("Stale withdraw");
        using var postWithdrawRequest = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{review.ReviewId}/withdraw", withdrawBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"99\"");
        using var postWithdrawResponse = await _client.SendAsync(postWithdrawRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, postWithdrawResponse.StatusCode);
        var withdrawContent = await postWithdrawResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(withdrawContent, "ETAG_PRECONDITION_FAILED");
    }

    [Fact]
    public async Task Scenario08_UpdateReview_Within7Days_SucceedsAndIncrementsVersionTo2WithEtag2()
    {
        var review = await CreateActiveReviewAsync();

        _fixture.TimeProvider.SetUtcNow(_fixture.CurrentTime.AddDays(2));
        try
        {
            var updateBody = new UpdateReviewRequest(4, "Updated comment within 7 days");
            using var putRequest = CreateRequest(HttpMethod.Put, $"/api/v1/reviews/{review.ReviewId}", updateBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
            using var putResponse = await _client.SendAsync(putRequest);

            Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
            Assert.NotNull(putResponse.Headers.ETag);
            Assert.Equal("\"2\"", putResponse.Headers.ETag.Tag);
            Assert.False(putResponse.Headers.ETag.IsWeak);

            var content = await putResponse.Content.ReadAsStringAsync();
            var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(content, JsonOptions);
            Assert.NotNull(detail);
            Assert.Equal(2, detail.Version);
            Assert.Equal(4, detail.Rating);
            Assert.Equal("Updated comment within 7 days", detail.Comment);
            Assert.NotNull(detail.UpdatedAt);
        }
        finally
        {
            _fixture.TimeProvider.Reset();
        }
    }

    [Fact]
    public async Task Scenario09_UpdateReview_After7Days_Returns422UnprocessableEntity()
    {
        var review = await CreateActiveReviewAsync();

        _fixture.TimeProvider.SetUtcNow(_fixture.CurrentTime.AddDays(8));
        try
        {
            var updateBody = new UpdateReviewRequest(4, "Attempt update after 7 days");
            using var putRequest = CreateRequest(HttpMethod.Put, $"/api/v1/reviews/{review.ReviewId}", updateBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
            using var putResponse = await _client.SendAsync(putRequest);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, putResponse.StatusCode);
            var content = await putResponse.Content.ReadAsStringAsync();
            AssertProblemTitle(content, "REVIEW_EDIT_WINDOW_EXPIRED");
        }
        finally
        {
            _fixture.TimeProvider.Reset();
        }
    }

    [Fact]
    public async Task Scenario10_WithdrawReview_TransitionsToWithdrawn_SubsequentEditReturns422()
    {
        var review = await CreateActiveReviewAsync();

        var withdrawBody = new WithdrawReviewRequest("Chủ động rút lại phản hồi");
        using var withdrawRequest = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{review.ReviewId}/withdraw", withdrawBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        using var withdrawResponse = await _client.SendAsync(withdrawRequest);

        Assert.Equal(HttpStatusCode.OK, withdrawResponse.StatusCode);
        Assert.NotNull(withdrawResponse.Headers.ETag);
        Assert.Equal("\"2\"", withdrawResponse.Headers.ETag.Tag);

        var withdrawContent = await withdrawResponse.Content.ReadAsStringAsync();
        var withdrawnDetail = JsonSerializer.Deserialize<ReviewDetailResponse>(withdrawContent, JsonOptions);
        Assert.NotNull(withdrawnDetail);
        Assert.Equal("Withdrawn", withdrawnDetail.AuthorState);
        Assert.Equal(2, withdrawnDetail.Version);
        Assert.NotNull(withdrawnDetail.WithdrawnAt);

        var updateBody = new UpdateReviewRequest(3, "Chỉnh sửa sau khi đã rút");
        using var editRequest = CreateRequest(HttpMethod.Put, $"/api/v1/reviews/{review.ReviewId}", updateBody, GenerateJwt(review.AuthorId), Guid.NewGuid().ToString("N"), ifMatch: "\"2\"");
        using var editResponse = await _client.SendAsync(editRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, editResponse.StatusCode);
        var editContent = await editResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(editContent, "REVIEW_ALREADY_WITHDRAWN");
    }

    [Fact]
    public async Task Scenario11_GetReviewDetail_Anonymous_Returns401Unauthorized()
    {
        var review = await CreateActiveReviewAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reviews/{review.ReviewId}");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Scenario12_GetReviewDetail_NonParticipant_Returns404NotFound()
    {
        var review = await CreateActiveReviewAsync();
        var nonParticipantId = Guid.NewGuid();

        using var nonParticipantRequest = CreateRequest(HttpMethod.Get, $"/api/v1/reviews/{review.ReviewId}", token: GenerateJwt(nonParticipantId));
        using var nonParticipantResponse = await _client.SendAsync(nonParticipantRequest);
        Assert.Equal(HttpStatusCode.NotFound, nonParticipantResponse.StatusCode);
        var content = await nonParticipantResponse.Content.ReadAsStringAsync();
        AssertProblemTitle(content, "REVIEW_NOT_FOUND");

        using var authorRequest = CreateRequest(HttpMethod.Get, $"/api/v1/reviews/{review.ReviewId}", token: GenerateJwt(review.AuthorId));
        using var authorResponse = await _client.SendAsync(authorRequest);
        Assert.Equal(HttpStatusCode.OK, authorResponse.StatusCode);

        using var subjectRequest = CreateRequest(HttpMethod.Get, $"/api/v1/reviews/{review.ReviewId}", token: GenerateJwt(review.SubjectId));
        using var subjectResponse = await _client.SendAsync(subjectRequest);
        Assert.Equal(HttpStatusCode.OK, subjectResponse.StatusCode);

        using var adminRequest = CreateRequest(HttpMethod.Get, $"/api/v1/reviews/{review.ReviewId}", token: GenerateJwt(Guid.NewGuid(), role: "Admin"));
        using var adminResponse = await _client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task Scenario13_CreateReview_CommentLengthBoundary_1000RunesPasses_1001RunesReturns400BadRequest()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(order1, item1, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        string comment1000 = new('a', 1000);
        var validBody = new CreateReviewRequest(order1, item1, 5, comment1000);
        using var validReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews", validBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var validRes = await _client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.Created, validRes.StatusCode);

        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(order2, item2, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        string comment1001 = new('a', 1001);
        var invalidBody = new CreateReviewRequest(order2, item2, 5, comment1001);
        using var invalidReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews", invalidBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var invalidRes = await _client.SendAsync(invalidReq);

        Assert.Equal(HttpStatusCode.BadRequest, invalidRes.StatusCode);
        var content = await invalidRes.Content.ReadAsStringAsync();
        AssertProblemTitle(content, "INVALID_COMMENT_LENGTH");
    }

    [Fact]
    public async Task Scenario14_CreateReview_RatingRange_ZeroOrSixReturns400BadRequest()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var bodyRating0 = new CreateReviewRequest(orderId, itemId, 0, "Rating 0 should fail");
        using var req0 = CreateRequest(HttpMethod.Post, "/api/v1/reviews", bodyRating0, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var res0 = await _client.SendAsync(req0);
        Assert.Equal(HttpStatusCode.BadRequest, res0.StatusCode);
        var content0 = await res0.Content.ReadAsStringAsync();
        AssertProblemTitle(content0, "INVALID_RATING");

        var bodyRating6 = new CreateReviewRequest(orderId, itemId, 6, "Rating 6 should fail");
        using var req6 = CreateRequest(HttpMethod.Post, "/api/v1/reviews", bodyRating6, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var res6 = await _client.SendAsync(req6);
        Assert.Equal(HttpStatusCode.BadRequest, res6.StatusCode);
        var content6 = await res6.Content.ReadAsStringAsync();
        AssertProblemTitle(content6, "INVALID_RATING");
    }

    [Fact]
    public async Task Scenario15_CreateReview_ServiceToken_RejectedWith403Forbidden()
    {
        var serviceActorId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, serviceActorId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var requestBody = new CreateReviewRequest(orderId, itemId, 5, "Service token review attempt");
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(serviceActorId, userType: "svc"), Guid.NewGuid().ToString("N"));
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        AssertProblemTitle(content, "FORBIDDEN_SERVICE_TOKEN");
    }

    [Fact]
    public async Task ReviewsController_AlternateRoutePrefixWithoutApiV1_ExposesAllEndpoints()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var createBody = new CreateReviewRequest(orderId, itemId, 5, "Alternate route test");
        using var createReq = CreateRequest(HttpMethod.Post, "/reviews", createBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var createRes = await _client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        Assert.NotNull(createRes.Headers.ETag);

        var createContent = await createRes.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(createContent, JsonOptions);
        Assert.NotNull(detail);

        using var getReq = CreateRequest(HttpMethod.Get, $"/reviews/{detail.ReviewId}", token: GenerateJwt(renterId));
        using var getRes = await _client.SendAsync(getReq);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);

        var updateBody = new UpdateReviewRequest(4, "Alternate route update");
        using var putReq = CreateRequest(HttpMethod.Put, $"/reviews/{detail.ReviewId}", updateBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        using var putRes = await _client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);
        Assert.Equal("\"2\"", putRes.Headers.ETag?.Tag);

        var withdrawBody = new WithdrawReviewRequest("Alternate route withdraw");
        using var withdrawReq = CreateRequest(HttpMethod.Post, $"/reviews/{detail.ReviewId}/withdraw", withdrawBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"), ifMatch: "\"2\"");
        using var withdrawRes = await _client.SendAsync(withdrawReq);
        Assert.Equal(HttpStatusCode.OK, withdrawRes.StatusCode);
        Assert.Equal("\"3\"", withdrawRes.Headers.ETag?.Tag);
    }

    private async Task<ReviewDetailResponse> CreateActiveReviewAsync()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(CreateCompletedSnapshot(orderId, itemId, renterId, supplierId, _fixture.CurrentTime.AddDays(-1)));

        var requestBody = new CreateReviewRequest(orderId, itemId, 5, "Thực hiện kiểm thử hợp lệ");
        using var request = CreateRequest(HttpMethod.Post, "/api/v1/reviews", requestBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(content, JsonOptions);
        Assert.NotNull(detail);
        return detail;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        object? body = null,
        string? token = null,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null)
        {
            string json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
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

    private static RentalItemReviewSnapshotDto CreateCompletedSnapshot(
        Guid orderId,
        Guid itemId,
        Guid renterId,
        Guid supplierId,
        DateTimeOffset completedAt)
    {
        return new RentalItemReviewSnapshotDto(
            OrderId: orderId,
            ItemId: itemId,
            RenterId: renterId,
            SupplierId: supplierId,
            EquipmentListingId: Guid.NewGuid(),
            Status: "Completed",
            CompletedAt: completedAt,
            OrderVersion: 1);
    }

    private static void AssertProblemTitle(string responseContent, string expectedTitle)
    {
        using var doc = JsonDocument.Parse(responseContent);
        Assert.True(doc.RootElement.TryGetProperty("title", out var titleProp), "ProblemDetails must contain title property.");
        Assert.Equal(expectedTitle, titleProp.GetString());
    }
}
