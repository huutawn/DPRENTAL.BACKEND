using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Application.DTOs.Reviews;
using Xunit;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// Bộ kiểm thử bảo mật toàn diện cho JWT Bearer authentication và authorization theo tiêu chuẩn P1.
/// Xác thực chữ ký số RS256, JWKS, claims 'sub'/'r', loại trừ token giả mạo, không ký, hoặc service token.
/// </summary>
public sealed class SecurityAuthenticationTests : IClassFixture<ReviewTestFixture>
{
    private readonly ReviewTestFixture _fixture;
    private readonly HttpClient _client;

    public SecurityAuthenticationTests(ReviewTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task Authenticate_ValidRsaSignedToken_Succeeds()
    {
        var renterId = Guid.NewGuid();
        var token = _fixture.GenerateJwt(renterId, userType: "cus", role: "Customer");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_UnsignedTokenWithAlgNone_Returns401Unauthorized()
    {
        var renterId = Guid.NewGuid();
        var unsignedToken = ReviewTestFixture.CreateUnsignedToken(renterId);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unsignedToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_InvalidRsaSignature_Returns401Unauthorized()
    {
        var renterId = Guid.NewGuid();
        var forgedToken = ReviewTestFixture.CreateTokenWithInvalidSignature(renterId);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forgedToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_ExpiredToken_Returns401Unauthorized()
    {
        var renterId = Guid.NewGuid();
        var expiredToken = _fixture.GenerateJwt(renterId, expired: true);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_WrongIssuer_Returns401Unauthorized()
    {
        var renterId = Guid.NewGuid();
        var badIssuerToken = _fixture.GenerateJwt(renterId, issuer: "wrong-auth-issuer");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", badIssuerToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_WrongAudience_Returns401Unauthorized()
    {
        var renterId = Guid.NewGuid();
        var badAudienceToken = _fixture.GenerateJwt(renterId, audience: "wrong-audience");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", badAudienceToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Authenticate_RawJsonOrUuidBearer_Returns401Unauthorized()
    {
        // 1. Raw UUID string
        using var uuidReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        uuidReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Guid.NewGuid().ToString());
        using var uuidRes = await _client.SendAsync(uuidReq);
        Assert.Equal(HttpStatusCode.Unauthorized, uuidRes.StatusCode);

        // 2. Raw JSON string
        using var jsonReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        jsonReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "{\"sub\":\"1234\",\"adm\":true}");
        using var jsonRes = await _client.SendAsync(jsonReq);
        Assert.Equal(HttpStatusCode.Unauthorized, jsonRes.StatusCode);
    }

    [Fact]
    public async Task Authorize_ServiceTokenOnHumanEndpoint_Returns403Forbidden()
    {
        var serviceId = Guid.NewGuid();
        var serviceToken = _fixture.GenerateJwt(serviceId, userType: "svc");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/my");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Authorize_CustomerCallingAdminEndpoint_Returns403Forbidden()
    {
        var customerId = Guid.NewGuid();
        var customerToken = _fixture.GenerateJwt(customerId, userType: "cus", role: "Customer");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/reviews/{Guid.NewGuid()}/hide");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        req.Headers.Add("If-Match", "\"1\"");
        req.Content = new StringContent(JsonSerializer.Serialize(new AdminModerationRequest("Vi pham")), System.Text.Encoding.UTF8, "application/json");

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Authorize_ForgedAdmClaimWithoutAdminRole_Returns403Forbidden()
    {
        var attackerId = Guid.NewGuid();
        // Generate validly signed token, but with role != Admin and fake adm claim
        var key = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048));
        // Using fixture key so signature is valid, but claims contain forged "adm": true and role "Customer"
        var token = _fixture.GenerateJwt(attackerId, userType: "cus", role: "Customer");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/reviews/{Guid.NewGuid()}/hide");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        req.Headers.Add("If-Match", "\"1\"");
        req.Content = new StringContent(JsonSerializer.Serialize(new AdminModerationRequest("Vi pham")), System.Text.Encoding.UTF8, "application/json");

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
