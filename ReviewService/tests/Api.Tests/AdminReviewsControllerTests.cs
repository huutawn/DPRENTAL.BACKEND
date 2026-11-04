using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Kiểm thử tích hợp cho AdminReviewsController xác minh chuẩn hóa quyền Admin theo Auth Contract:
/// - Claim r = "adm" và r = "Admin" được phép truy cập đầy đủ các endpoint Admin.
/// - Service token (u = "svc") luôn bị từ chối 403 Forbidden ngay cả khi kèm r = "adm".
/// - Claim giả mạo adm = "true" không có role hợp lệ luôn bị từ chối 403 Forbidden.
/// - Khách hàng thông thường (role = "Customer") luôn bị từ chối 403 Forbidden.
/// </summary>
public sealed class AdminReviewsControllerTests : IClassFixture<ReviewTestFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public AdminReviewsControllerTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task GetReports_WithRoleAdm_Returns200Ok()
    {
        var adminId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(adminId, userType: "cus", role: "adm");

        using var request = CreateRequest(HttpMethod.Get, "/api/v1/admin/reviews/reports", token: token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReports_WithRoleAdmin_Returns200Ok()
    {
        var adminId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(adminId, userType: "cus", role: "Admin");

        using var request = CreateRequest(HttpMethod.Get, "/api/v1/admin/reviews/reports", token: token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReports_WithServiceToken_Returns403Forbidden_EvenIfRoleIsAdm()
    {
        var serviceId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(serviceId, userType: "svc", role: "adm");

        using var request = CreateRequest(HttpMethod.Get, "/api/v1/admin/reviews/reports", token: token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReports_WithForgedAdmBooleanClaim_Returns403Forbidden()
    {
        var attackerId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(
            attackerId,
            userType: "cus",
            role: "Customer",
            extraClaims: new[] { new Claim("adm", "true") });

        using var request = CreateRequest(HttpMethod.Get, "/api/v1/admin/reviews/reports", token: token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReports_WithNormalCustomerRole_Returns403Forbidden()
    {
        var customerId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(customerId, userType: "cus", role: "Customer");

        using var request = CreateRequest(HttpMethod.Get, "/api/v1/admin/reviews/reports", token: token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HideReview_WithRoleAdm_SuccessfullyHidesReview()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review will be hidden by adm"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Admin với r="adm" gọi hide
        var adminId = Guid.NewGuid();
        var adminToken = _fixture.GenerateJwt(adminId, userType: "cus", role: "adm");
        var hideBody = new AdminModerationRequest("Vi phạm chuẩn mực cộng đồng.");

        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/hide",
            hideBody,
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        using var hideRes = await _client.SendAsync(hideReq);
        Assert.Equal(HttpStatusCode.OK, hideRes.StatusCode);

        var content = await hideRes.Content.ReadAsStringAsync();
        var modRes = JsonSerializer.Deserialize<AdminModerationResponse>(content, JsonOptions);
        Assert.NotNull(modRes);
        Assert.Equal("Hidden", modRes.ModerationState);
        Assert.Equal(2, modRes.Version);
    }

    [Fact]
    public async Task HideReview_WithServiceToken_Returns403Forbidden()
    {
        var serviceId = Guid.NewGuid();
        var serviceToken = _fixture.GenerateJwt(serviceId, userType: "svc", role: "adm");
        var hideBody = new AdminModerationRequest("Lý do");

        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{Guid.NewGuid()}/hide",
            hideBody,
            serviceToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        using var res = await _client.SendAsync(hideReq);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task HideReview_WithForgedAdmBooleanClaim_Returns403Forbidden()
    {
        var attackerId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(
            attackerId,
            userType: "cus",
            role: "Customer",
            extraClaims: new[] { new Claim("adm", "true") });
        var hideBody = new AdminModerationRequest("Lý do");

        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{Guid.NewGuid()}/hide",
            hideBody,
            token,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");

        using var res = await _client.SendAsync(hideReq);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task RestoreReview_WithRoleAdm_SuccessfullyRestoresReview()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 4, "Review will be hidden then restored"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        var adminId = Guid.NewGuid();
        var adminToken = _fixture.GenerateJwt(adminId, userType: "cus", role: "adm");

        // 1. Hide
        using var hideReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/hide",
            new AdminModerationRequest("Tạm ẩn"),
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"1\"");
        var hideRes = await _client.SendAsync(hideReq);
        Assert.Equal(HttpStatusCode.OK, hideRes.StatusCode);

        // 2. Restore với r="adm"
        using var restoreReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{detail.ReviewId}/restore",
            new AdminModerationRequest("Đã duyệt lại hợp lệ"),
            adminToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"2\"");

        using var restoreRes = await _client.SendAsync(restoreReq);
        Assert.Equal(HttpStatusCode.OK, restoreRes.StatusCode);

        var content = await restoreRes.Content.ReadAsStringAsync();
        var modRes = JsonSerializer.Deserialize<AdminModerationResponse>(content, JsonOptions);
        Assert.NotNull(modRes);
        Assert.Equal("Visible", modRes.ModerationState);
        Assert.Equal(3, modRes.Version);
    }

    [Fact]
    public async Task RestoreReview_WithServiceToken_Returns403Forbidden()
    {
        var serviceId = Guid.NewGuid();
        var serviceToken = _fixture.GenerateJwt(serviceId, userType: "svc", role: "adm");

        using var restoreReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/{Guid.NewGuid()}/restore",
            new AdminModerationRequest("Khôi phục"),
            serviceToken,
            Guid.NewGuid().ToString("N"),
            ifMatch: "\"2\"");

        using var res = await _client.SendAsync(restoreReq);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task ResolveReport_WithRoleAdm_SuccessfullyResolvesReport()
    {
        var renterId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        _fixture.RentalClient.SetSnapshot(new RentalItemReviewSnapshotDto(
            orderId, itemId, renterId, supplierId, Guid.NewGuid(), "Completed", _fixture.CurrentTime.AddDays(-1), 1));

        using var postReq = CreateRequest(
            HttpMethod.Post,
            "/api/v1/reviews",
            new CreateReviewRequest(orderId, itemId, 5, "Review to report"),
            _fixture.GenerateJwt(renterId),
            Guid.NewGuid().ToString("N"));

        var postRes = await _client.SendAsync(postReq);
        var detail = JsonSerializer.Deserialize<ReviewDetailResponse>(await postRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // User reports review
        var reporterId = Guid.NewGuid();
        using var reportReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/reviews/{detail.ReviewId}/reports",
            new CreateReviewReportRequest("Nội dung spam"),
            _fixture.GenerateJwt(reporterId),
            Guid.NewGuid().ToString("N"));

        var reportRes = await _client.SendAsync(reportReq);
        Assert.Equal(HttpStatusCode.Created, reportRes.StatusCode);
        var report = JsonSerializer.Deserialize<ReviewReportResponse>(await reportRes.Content.ReadAsStringAsync(), JsonOptions)!;

        // Admin với r="adm" resolves report
        var adminId = Guid.NewGuid();
        var adminToken = _fixture.GenerateJwt(adminId, userType: "cus", role: "adm");

        using var resolveReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/reports/{report.Id}/resolve",
            new ResolveReviewReportRequest("ReviewedDismissed"),
            adminToken,
            Guid.NewGuid().ToString("N"));

        using var resolveRes = await _client.SendAsync(resolveReq);
        Assert.Equal(HttpStatusCode.OK, resolveRes.StatusCode);

        var resolved = JsonSerializer.Deserialize<ReviewReportResponse>(await resolveRes.Content.ReadAsStringAsync(), JsonOptions)!;
        Assert.Equal("ReviewedDismissed", resolved.Status);
    }

    [Fact]
    public async Task ResolveReport_WithServiceToken_Returns403Forbidden()
    {
        var serviceId = Guid.NewGuid();
        var serviceToken = _fixture.GenerateJwt(serviceId, userType: "svc", role: "adm");

        using var resolveReq = CreateRequest(
            HttpMethod.Post,
            $"/api/v1/admin/reviews/reports/{Guid.NewGuid()}/resolve",
            new ResolveReviewReportRequest("ReviewedDismissed"),
            serviceToken,
            Guid.NewGuid().ToString("N"));

        using var res = await _client.SendAsync(resolveReq);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
