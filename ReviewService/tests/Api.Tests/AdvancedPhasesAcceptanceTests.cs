using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Dprental.ReviewService.Domain.Entities;
using Dprental.ReviewService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử chấp nhận toàn diện cho các Phase 4, 5, 6, 7 (Discovery, Two-Way, Moderation, Media).
/// </summary>
public sealed class AdvancedPhasesAcceptanceTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public AdvancedPhasesAcceptanceTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    #region Phase 4: Discovery & Pending Reviews

    [Fact]
    public async Task Phase4_GetPendingReviews_ReturnsEligibleItemsNotYetReviewed()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        // Nạp projection vào cơ sở dữ liệu
        using (var scope = _fixture.Services.CreateScope())
        {
            var projectionRepo = scope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
            var projection = new ReviewProjection(
                Guid.NewGuid(),
                orderId,
                itemId,
                renterId,
                supplierId,
                listingId,
                _fixture.CurrentTime.AddDays(-2),
                _fixture.CurrentTime.AddDays(28),
                true,
                _fixture.CurrentTime.AddDays(-2));
            await projectionRepo.UpsertAsync(projection);
        }

        // Gọi GET /api/v1/reviews/pending
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(renterId));
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<PendingReviewItemResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.Contains(paged.Items, i => i.ItemId == itemId && i.CounterpartId == supplierId);
    }

    [Fact]
    public async Task Phase4_GetPendingReviews_ExcludesAlreadyReviewedItems()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using (var scope = _fixture.Services.CreateScope())
        {
            var projectionRepo = scope.ServiceProvider.GetRequiredService<IReviewProjectionRepository>();
            var projection = new ReviewProjection(
                Guid.NewGuid(), orderId, itemId, renterId, supplierId, listingId,
                _fixture.CurrentTime.AddDays(-1), _fixture.CurrentTime.AddDays(29), true, _fixture.CurrentTime.AddDays(-1));
            await projectionRepo.UpsertAsync(projection);
        }

        // Tạo review
        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Đã viết đánh giá"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        var postRes = await _client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);

        // Gọi GET /api/v1/reviews/pending -> item không còn xuất hiện
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(renterId));
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var paged = JsonSerializer.Deserialize<PagedResult<PendingReviewItemResponse>>(content, JsonOptions);

        Assert.NotNull(paged);
        Assert.DoesNotContain(paged.Items, i => i.ItemId == itemId);
    }

    #endregion

    #region Phase 5: Two-Way Reviews & Private Renter Reputation

    [Fact]
    public async Task Phase5_SupplierReviewsRenter_Success_DirectionIsSupplierToRenter()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var listingId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, listingId, "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        // Supplier gửi đánh giá về Renter
        var createReq = new CreateReviewRequest(orderId, itemId, 5, "Khách hàng thuê máy giữ gìn rất cẩn thận, hoàn trả đúng hạn!");
        using var req = CreateRequest(HttpMethod.Post, "/api/v1/reviews", createReq, GenerateJwt(supplierId), Guid.NewGuid().ToString("N"));
        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var content = await res.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(content, JsonOptions);

        Assert.NotNull(detail);
        Assert.Equal("supplier_to_renter", detail.Direction);
        Assert.Equal(supplierId, detail.AuthorId);
        Assert.Equal(renterId, detail.SubjectId);
    }

    [Fact]
    public async Task Phase5_GetRenterReputation_PrivateToRenter_ReturnsCalculatedScore()
    {
        var renterId = Guid.NewGuid();
        var supplier1 = Guid.NewGuid();
        var supplier2 = Guid.NewGuid();

        // Supplier 1 đánh giá renter 5 sao
        var order1 = Guid.NewGuid();
        var item1 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            order1, item1, renterId, supplier1, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-2), 1));
        using var req1 = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(order1, item1, 5, "Rất tốt"), GenerateJwt(supplier1), Guid.NewGuid().ToString("N"));
        await _client.SendAsync(req1);

        // Supplier 2 đánh giá renter 4 sao
        var order2 = Guid.NewGuid();
        var item2 = Guid.NewGuid();
        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            order2, item2, renterId, supplier2, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-2), 1));
        using var req2 = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(order2, item2, 4, "Khá tốt"), GenerateJwt(supplier2), Guid.NewGuid().ToString("N"));
        await _client.SendAsync(req2);

        // Renter truy vấn uy tín của mình
        using var repReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my/reputation");
        repReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(renterId));
        using var repRes = await _client.SendAsync(repReq);

        Assert.Equal(HttpStatusCode.OK, repRes.StatusCode);
        var content = await repRes.Content.ReadAsStringAsync();
        var rep = JsonSerializer.Deserialize<RenterReputationResponse>(content, JsonOptions);

        Assert.NotNull(rep);
        Assert.Equal(renterId, rep.RenterId);
        Assert.Equal(2, rep.ReviewCount);
        Assert.Equal(4.5, rep.AverageRating);
    }

    #endregion

    #region Phase 6: Reporting & Moderation

    [Fact]
    public async Task Phase6_ReportReview_Success_ThenDuplicateReturns409()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Bình luận bình thường"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Reporter báo cáo vi phạm
        var reporterId = Guid.NewGuid();
        var reportBody = new CreateReviewReportRequest("Nội dung mang tính chất spam quảng cáo.");
        using var repReq = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{detail.ReviewId}/reports", reportBody, GenerateJwt(reporterId), Guid.NewGuid().ToString("N"));
        using var repRes = await _client.SendAsync(repReq);

        Assert.Equal(HttpStatusCode.Created, repRes.StatusCode);

        // Báo cáo lần 2 bởi cùng reporter -> 409 Conflict
        using var dupReq = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{detail.ReviewId}/reports", reportBody, GenerateJwt(reporterId), Guid.NewGuid().ToString("N"));
        using var dupRes = await _client.SendAsync(dupReq);

        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);
    }

    [Fact]
    public async Task Phase6_AdminHideReview_Success_AndNonAdminForbidden()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Đánh giá sắp bị ẩn"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Người dùng thường gọi endpoint admin/hide -> 403 Forbidden
        var normalUser = Guid.NewGuid();
        var hideBody = new AdminModerationRequest("Vi phạm tiêu chuẩn.");
        using var forbidReq = CreateRequest(HttpMethod.Post, $"/api/v1/admin/reviews/{detail.ReviewId}/hide", hideBody, GenerateJwt(normalUser, role: "customer"), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        using var forbidRes = await _client.SendAsync(forbidReq);
        Assert.Equal(HttpStatusCode.Forbidden, forbidRes.StatusCode);

        // Admin gọi endpoint admin/hide -> 200 OK
        var adminId = Guid.NewGuid();
        using var adminReq = CreateRequest(HttpMethod.Post, $"/api/v1/admin/reviews/{detail.ReviewId}/hide", hideBody, GenerateJwt(adminId, role: "Admin"), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        using var adminRes = await _client.SendAsync(adminReq);

        Assert.Equal(HttpStatusCode.OK, adminRes.StatusCode);
        var content = await adminRes.Content.ReadAsStringAsync();
        var modRes = JsonSerializer.Deserialize<AdminModerationResponse>(content, JsonOptions);

        Assert.NotNull(modRes);
        Assert.Equal("Hidden", modRes.ModerationState);
        Assert.Equal(2, modRes.Version);
    }

    [Fact]
    public async Task Phase6_AdminRestoreReview_Success_UpdatesModerationStateToVisible()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Sắp bị ẩn rồi phục hồi"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        var adminId = Guid.NewGuid();
        // 1. Hide
        using var hideReq = CreateRequest(HttpMethod.Post, $"/api/v1/admin/reviews/{detail.ReviewId}/hide",
            new AdminModerationRequest("Lý do ẩn"), GenerateJwt(adminId, role: "Admin"), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        await _client.SendAsync(hideReq);

        // 2. Restore
        using var restoreReq = CreateRequest(HttpMethod.Post, $"/api/v1/admin/reviews/{detail.ReviewId}/restore",
            new AdminModerationRequest("Khôi phục"), GenerateJwt(adminId, role: "Admin"), Guid.NewGuid().ToString("N"), ifMatch: "\"2\"");
        using var restoreRes = await _client.SendAsync(restoreReq);

        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);
        var content = await restoreRes.Content.ReadAsStringAsync();
        var modRes = JsonSerializer.Deserialize<AdminModerationResponse>(content, JsonOptions);

        Assert.NotNull(modRes);
        Assert.Equal("Visible", modRes.ModerationState);
        Assert.Equal(3, modRes.Version);
    }

    #endregion

    #region Phase 7: Media Attach & Detach

    [Fact]
    public async Task Phase7_AttachAndDetachMedia_Success()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(HttpMethod.Post, "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Đánh giá có ảnh"), GenerateJwt(renterId), Guid.NewGuid().ToString("N"));
        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // 1. Đính kèm ảnh
        var mediaId = Guid.NewGuid();
        var attachBody = new AttachMediaRequest(mediaId, "image/jpeg");
        using var attachReq = CreateRequest(HttpMethod.Post, $"/api/v1/reviews/{detail.ReviewId}/media",
            attachBody, GenerateJwt(renterId), Guid.NewGuid().ToString("N"), ifMatch: "\"1\"");
        using var attachRes = await _client.SendAsync(attachReq);

        Assert.Equal(HttpStatusCode.Created, attachRes.StatusCode);
        var mediaRes = JsonSerializer.Deserialize<ReviewMediaResponse>(await attachRes.Content.ReadAsStringAsync(), JsonOptions);
        Assert.NotNull(mediaRes);
        Assert.Equal(mediaId, mediaRes.MediaId);

        // 2. Gỡ ảnh
        using var detachReq = CreateRequest(HttpMethod.Delete, $"/api/v1/reviews/{detail.ReviewId}/media/{mediaId}",
            null, GenerateJwt(renterId), null, ifMatch: "\"2\"");
        using var detachRes = await _client.SendAsync(detachReq);

        Assert.Equal(HttpStatusCode.NoContent, detachRes.StatusCode);
    }

    #endregion

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

    private string GenerateJwt(Guid userId, string? userType = "cus", string? role = null)
    {
        return _fixture.GenerateJwt(userId, userType, role);
    }
}
