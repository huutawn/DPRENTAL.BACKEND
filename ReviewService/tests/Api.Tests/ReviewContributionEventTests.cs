using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Application.Kafka.Events;
using Dprental.ReviewService.Common.Enums;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử toàn diện cho Canonical Full-State Contribution Event trên topic review.events.v1:
/// - Xác minh builder ReviewEventFactory.CreateContributionEvent tuân thủ chính xác ma trận publicEligible.
/// - Xác minh bảo mật quyền riêng tư: payload tuyệt đối không chứa PII, danh tính người thuê hay comment.
/// - Xác minh mọi thao tác đột biến review (Create, Update, Withdraw, Hide, Restore, Resolve report to hide)
///   đều phát ra sự kiện canonical full-state đồng nhất với partition key là review.ListingId.ToString("D").
/// </summary>
public sealed class ReviewContributionEventTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public ReviewContributionEventTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    #region Unit Tests - ReviewEventFactory & Review Entity Aliases

    [Fact]
    public void CreateContributionEvent_RenterToSupplierListing_Active_Visible_ReturnsPublicEligibleTrue()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.renter_to_supplier_listing,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible);

        var evt = ReviewEventFactory.CreateContributionEvent(review);

        Assert.Equal(review.Id, evt.ReviewId);
        Assert.Equal(review.Version, evt.ReviewVersion);
        Assert.Equal(review.ListingId, evt.ListingId);
        Assert.Equal(review.EquipmentListingId, evt.ListingId);
        Assert.Equal(review.SubjectId, evt.SupplierId);
        Assert.Equal(review.SupplierId, evt.SupplierId);
        Assert.Equal(review.Rating, evt.Rating);
        Assert.True(evt.PublicEligible);
    }

    [Fact]
    public void CreateContributionEvent_SupplierToRenter_ReturnsPublicEligibleFalse()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.supplier_to_renter,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible);

        var evt = ReviewEventFactory.CreateContributionEvent(review);

        Assert.False(evt.PublicEligible);
        // Với supplier_to_renter, SupplierId là AuthorId
        Assert.Equal(review.AuthorId, evt.SupplierId);
    }

    [Fact]
    public void CreateContributionEvent_WithdrawnReview_ReturnsPublicEligibleFalse()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.renter_to_supplier_listing,
            authorState: AuthorState.Withdrawn,
            moderationState: ModerationState.Visible);

        var evt = ReviewEventFactory.CreateContributionEvent(review);

        Assert.False(evt.PublicEligible);
    }

    [Fact]
    public void CreateContributionEvent_HiddenReview_ReturnsPublicEligibleFalse()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.renter_to_supplier_listing,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Hidden);

        var evt = ReviewEventFactory.CreateContributionEvent(review);

        Assert.False(evt.PublicEligible);
    }

    [Fact]
    public void CreateContributionEvent_WithdrawnAndHiddenReview_ReturnsPublicEligibleFalse()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.renter_to_supplier_listing,
            authorState: AuthorState.Withdrawn,
            moderationState: ModerationState.Hidden);

        var evt = ReviewEventFactory.CreateContributionEvent(review);

        Assert.False(evt.PublicEligible);
    }

    [Fact]
    public void CreateContributionEvent_ThrowsArgumentNullException_WhenReviewIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => ReviewEventFactory.CreateContributionEvent(null!));
    }

    [Fact]
    public void ContributionEvent_SerializedJson_DoesNotContainPiiOrComment()
    {
        var review = CreateSampleReview(
            direction: ReviewDirection.renter_to_supplier_listing,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            comment: "This is a private comment with sensitive info");

        var evt = ReviewEventFactory.CreateContributionEvent(review);
        var json = JsonSerializer.Serialize(evt, JsonOptions);

        // Xác nhận chứa các trường hợp lệ
        Assert.Contains("reviewId", json);
        Assert.Contains("reviewVersion", json);
        Assert.Contains("listingId", json);
        Assert.Contains("supplierId", json);
        Assert.Contains("rating", json);
        Assert.Contains("publicEligible", json);

        // Xác nhận KHÔNG chứa bình luận hay thông tin định danh cá nhân
        Assert.DoesNotContain("comment", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("orderId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adminReason", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive info", json, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Integration Tests - Outbox Mutations

    [Fact]
    public async Task CreateReview_EmitsCanonicalContributionEvent_ToReviewEventsV1_WithListingIdPartitionKey()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var request = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Nội dung nhận xét tích cực"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await response.Content.ReadAsStringAsync(), JsonOptions)!;

        // Truy vấn OutboxMessage
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var outboxList = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var outbox = outboxList.FirstOrDefault(m => m.EventType == "ReviewCreated" && m.PartitionKey == listingId.ToString("D"));
        Assert.NotNull(outbox);
        Assert.Equal(ReviewEventFactory.PublicEventsTopic, outbox.Topic);
        Assert.Equal(listingId.ToString("D"), outbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(outbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(1, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.Equal(supplierId, payload.SupplierId);
        Assert.Equal(5, payload.Rating);
        Assert.True(payload.PublicEligible);

        // Đảm bảo không chứa PII
        Assert.DoesNotContain("Nội dung nhận xét", outbox.Payload);
    }

    [Fact]
    public async Task UpdateReview_EmitsCanonicalContributionEvent_ToReviewEventsV1_WithListingIdPartitionKey()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 4, "Initial review"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Cập nhật review
        using var putReq = CreateRequest(
            HttpMethod.Put,
            $"/api/v1/reviews/{detail.ReviewId}",
            new UpdateReviewRequest(5, "Updated positive comment"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        var putRes = await _client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var updateOutbox = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic && m.EventType == "ReviewUpdated" && m.PartitionKey == listingId.ToString("D"))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(updateOutbox);
        Assert.Equal(listingId.ToString("D"), updateOutbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(updateOutbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(2, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.Equal(5, payload.Rating);
        Assert.True(payload.PublicEligible);
    }

    [Fact]
    public async Task WithdrawReview_EmitsCanonicalContributionEvent_WithPublicEligibleFalse()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review to withdraw"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Rút review
        using var withdrawReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/reviews/{detail.ReviewId}/withdraw",
            new WithdrawReviewRequest("Không muốn hiển thị nữa"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        var withdrawRes = await _client.SendAsync(withdrawReq);
        Assert.Equal(HttpStatusCode.OK, withdrawRes.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var withdrawOutbox = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic && m.EventType == "ReviewWithdrawn" && m.PartitionKey == listingId.ToString("D"))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(withdrawOutbox);
        Assert.Equal(listingId.ToString("D"), withdrawOutbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(withdrawOutbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(2, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.False(payload.PublicEligible);
    }

    [Fact]
    public async Task HideReview_EmitsCanonicalContributionEvent_WithPublicEligibleFalse()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review to hide"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Admin hide review
        var adminToken = _fixture.GenerateJwt(Guid.NewGuid(), userType: "cus", role: "adm");
        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/hide",
            new AdminModerationRequest("Ẩn vi phạm"),
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        var hideRes = await _client.SendAsync(hideReq);
        Assert.Equal(HttpStatusCode.OK, hideRes.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var hideOutbox = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic && m.EventType == "ReviewHidden" && m.PartitionKey == listingId.ToString("D"))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(hideOutbox);
        Assert.Equal(listingId.ToString("D"), hideOutbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(hideOutbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(2, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.False(payload.PublicEligible);
    }

    [Fact]
    public async Task RestoreReview_EmitsCanonicalContributionEvent_WithPublicEligibleTrue()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review to hide and restore"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        var adminToken = _fixture.GenerateJwt(Guid.NewGuid(), userType: "cus", role: "adm");

        // 1. Hide
        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/hide",
            new AdminModerationRequest("Tạm ẩn"),
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");
        await _client.SendAsync(hideReq);

        // 2. Restore
        using var restoreReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/restore",
            new AdminModerationRequest("Khôi phục"),
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"2\"");

        var restoreRes = await _client.SendAsync(restoreReq);
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var restoreOutbox = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic && m.EventType == "ReviewRestored" && m.PartitionKey == listingId.ToString("D"))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(restoreOutbox);
        Assert.Equal(listingId.ToString("D"), restoreOutbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(restoreOutbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(3, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.True(payload.PublicEligible);
    }

    [Fact]
    public async Task ResolveReportToHide_EmitsCanonicalContributionEvent_WithPublicEligibleFalse()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review reported then hidden"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Báo cáo review
        using var reportReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/reviews/{detail.ReviewId}/reports",
            new CreateReviewReportRequest("Spam nặng"),
            _fixture.GenerateJwt(Guid.NewGuid()),
            Guid.NewGuid().ToString("N"));

        var reportRes = await _client.SendAsync(reportReq);
        var report = JsonSerializer.Deserialize<ReviewReportResponse>(await reportRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Admin resolve với ReviewedHidden
        var adminToken = _fixture.GenerateJwt(Guid.NewGuid(), userType: "cus", role: "adm");
        using var resolveReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/reports/{report.Id}/resolve",
            new ResolveReviewReportRequest("ReviewedHidden"),
            adminToken,
            Guid.NewGuid().ToString("N"));

        var resolveRes = await _client.SendAsync(resolveReq);
        Assert.Equal(HttpStatusCode.OK, resolveRes.StatusCode);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReviewServiceDbContext>();

        var hideOutbox = await db.OutboxMessages
            .Where(m => m.Topic == ReviewEventFactory.PublicEventsTopic && m.EventType == "ReviewHidden" && m.PartitionKey == listingId.ToString("D"))
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(hideOutbox);
        Assert.Equal(listingId.ToString("D"), hideOutbox.PartitionKey);

        var payload = JsonSerializer.Deserialize<ReviewPublicContributionEvent>(hideOutbox.Payload, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(detail.ReviewId, payload.ReviewId);
        Assert.Equal(2, payload.ReviewVersion);
        Assert.Equal(listingId, payload.ListingId);
        Assert.False(payload.PublicEligible);
    }

    #endregion

    private static Review CreateSampleReview(
        ReviewDirection direction,
        AuthorState authorState,
        ModerationState moderationState,
        string? comment = "Valid test review comment")
    {
        var now = DateTimeOffset.UtcNow;
        return new Review(
            id: Guid.NewGuid(),
            orderId: Guid.NewGuid(),
            itemId: Guid.NewGuid(),
            direction: direction,
            authorId: Guid.NewGuid(),
            subjectId: Guid.NewGuid(),
            equipmentListingId: Guid.NewGuid(),
            rating: 5,
            comment: comment,
            authorState: authorState,
            moderationState: moderationState,
            version: 1,
            createdAt: now,
            updatedAt: null,
            withdrawnAt: authorState == AuthorState.Withdrawn ? now : null,
            adminHiddenAt: moderationState == ModerationState.Hidden ? now : null,
            adminReason: moderationState == ModerationState.Hidden ? "Admin reason" : null,
            canEditUntil: now.AddDays(7));
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        object? body = null,
        string? token = null,
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }
}
