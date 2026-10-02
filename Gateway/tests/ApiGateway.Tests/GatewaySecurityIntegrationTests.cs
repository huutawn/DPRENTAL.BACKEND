using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.Gateway.Security;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Bộ kiểm thử tích hợp toàn diện cho các tính năng Bảo mật và Bảo vệ Request của API Gateway (DPR-78).
/// Kiểm chứng 4 tiêu chí nghiệm thu cốt lõi (Acceptance Criteria) cùng các kịch bản biên (Edge Cases):
/// 1. Client tự gửi X-User-Id giả -> downstream vẫn nhận UserId từ token thật.
/// 2. Origin không nằm trong whitelist -> request bị reject theo CORS policy.
/// 3. JWT hợp lệ -> identity context được forward chính xác (UserId, PartyId, Roles, CorrelationId, ServiceName).
/// 4. JWT không hợp lệ -> không bao giờ forward identity downstream.
/// 5. Request Size Protection -> chặn request có payload vượt quá giới hạn.
/// 6. Security Headers -> kiểm chứng sự hiện diện của các header bảo mật trên response.
/// </summary>
public sealed class GatewaySecurityIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string Audience = "dprental-clients";
    private const string WhitelistedOrigin = "https://app.dprental.vn";
    private const string DisallowedOrigin = "https://evil-attacker.com";

    [Fact]
    public async Task ClientSpoofingUserIdDownstreamReceivesRealUserIdFromToken()
    {
        // Tiêu chí 1: Client tự gửi X-User-Id giả -> downstream vẫn nhận UserId từ token thật.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        string realUserId = Guid.NewGuid().ToString("D");
        string validToken = CreateToken(signingKey, "key-1", Issuer, Audience, realUserId);

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");
        request.Headers.Authorization = new("Bearer", validToken);
        // Client cố tình gửi tiêu đề X-User-Id giả mạo
        request.Headers.Add(GatewayHeaderConstants.UserId, "fake-attacker-user-id");
        request.Headers.Add(GatewayHeaderConstants.ServiceName, "FakeInternalService");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Downstream bắt buộc phải nhận UserId từ token thật, tiêu đề giả mạo đã bị ghi đè
        Assert.Equal(realUserId, upstream.LastRequest.Headers[GatewayHeaderConstants.UserId]);
        // X-Service-Name giả mạo của client phải bị ghi đè bằng ApiGateway
        Assert.Equal("ApiGateway", upstream.LastRequest.Headers[GatewayHeaderConstants.ServiceName]);
    }

    [Fact]
    public async Task DisallowedOriginRejectedByCorsPolicyWithForbidden()
    {
        // Tiêu chí 2: Origin không nằm trong whitelist -> request bị reject theo CORS policy (403 Forbidden).
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        // Gửi request từ nguồn gốc không nằm trong whitelist
        request.Headers.Add("Origin", DisallowedOrigin);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        GatewayErrorResponse? error = await response.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(GatewayError.CorsOriginDenied.Code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        // Upstream không bao giờ được nhận request vi phạm CORS
        Assert.Null(upstream.LastRequest.Method);
    }

    [Fact]
    public async Task AllowedOriginReceivesSuccessfulResponseWithCorsHeaders()
    {
        // Kịch bản biên CORS: Origin nằm trong whitelist -> request được thông qua và nhận header CORS.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", WhitelistedOrigin);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WhitelistedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task GatewayCorsPolicyReplacesCorsHeadersFromUpstream()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        upstream.ResponseHeaders["Access-Control-Allow-Origin"] = "http://localhost:3000";
        upstream.ResponseHeaders["Access-Control-Allow-Credentials"] = "false";
        upstream.ResponseHeaders["Access-Control-Allow-Methods"] = "GET";
        upstream.ResponseHeaders["Access-Control-Expose-Headers"] = "X-Upstream-Only";
        upstream.ResponseHeaders["Vary"] = "Accept-Encoding";

        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", WhitelistedOrigin);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WhitelistedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.Equal("X-Correlation-ID, Retry-After", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        string vary = string.Join(",", response.Headers.GetValues("Vary"));
        Assert.Contains("Accept-Encoding", vary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Origin", vary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreflightOptionsAllowedOriginReturns204WithPreflightHeaders()
    {
        // Kịch bản Preflight CORS: OPTIONS request với Origin hợp lệ trả về 204 No Content cùng các header quy định.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/auth/sessions");
        preflight.Headers.Add("Origin", WhitelistedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "Authorization, Content-Type");

        using HttpResponseMessage response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WhitelistedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.True(response.Headers.Contains("Access-Control-Allow-Methods"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task PreflightOptionsDisallowedOriginRejectedWith403()
    {
        // Kịch bản Preflight CORS vi phạm: OPTIONS từ Origin lạ bị Gateway reject 403 Forbidden.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/auth/sessions");
        preflight.Headers.Add("Origin", DisallowedOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        using HttpResponseMessage response = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        GatewayErrorResponse? error = await response.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(GatewayError.CorsOriginDenied.Code, error.Code);
    }

    [Fact]
    public async Task ValidTokenForwardsCompleteIdentityContextDownstream()
    {
        // Tiêu chí 3: JWT hợp lệ -> identity context được forward chính xác xuống downstream.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        string expectedUserId = Guid.NewGuid().ToString("D");
        string expectedPartyId = "party-dprental-org-888";
        string[] expectedRoles = ["admin", "manager"];

        string token = CreateToken(
            signingKey,
            keyId: "key-1",
            issuer: Issuer,
            audience: Audience,
            userId: expectedUserId,
            partyId: expectedPartyId,
            roles: expectedRoles);

        Guid correlationId = Guid.NewGuid();
        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(GatewayHeaderConstants.CorrelationId, correlationId.ToString("D"));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Kiểm tra tính toàn vẹn của Identity Context tại downstream
        Assert.Equal(expectedUserId, upstream.LastRequest.Headers[GatewayHeaderConstants.UserId]);
        Assert.Equal(expectedPartyId, upstream.LastRequest.Headers[GatewayHeaderConstants.PartyId]);
        Assert.Equal(correlationId.ToString("D"), upstream.LastRequest.Headers[GatewayHeaderConstants.CorrelationId]);
        Assert.Equal("ApiGateway", upstream.LastRequest.Headers[GatewayHeaderConstants.ServiceName]);

        // Kiểm tra Roles được chuyển tiếp
        string? receivedRole = upstream.LastRequest.Headers[GatewayHeaderConstants.Role];
        Assert.NotNull(receivedRole);
        Assert.Contains("admin", receivedRole, StringComparison.Ordinal);
        Assert.Contains("manager", receivedRole, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidTokenNeverForwardsIdentityDownstream()
    {
        // Tiêu chí 4: JWT không hợp lệ -> không bao giờ forward identity downstream.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        // 1. Request có token giả mạo chữ ký
        using RSA untrustedKey = RSA.Create(2048);
        string invalidToken = CreateToken(untrustedKey, "untrusted-key", Issuer, Audience, "fake-user");

        using HttpRequestMessage invalidTokenRequest = new(HttpMethod.Get, "/protected/orders");
        invalidTokenRequest.Headers.Authorization = new("Bearer", invalidToken);
        invalidTokenRequest.Headers.Add(GatewayHeaderConstants.UserId, "spoofed-user-id");

        using HttpResponseMessage response1 = await client.SendAsync(invalidTokenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, response1.StatusCode);
        // Upstream không nhận được request được ủy quyền
        Assert.Null(upstream.LastRequest.Method);

        // 2. Request ẩn danh trên route công khai gửi header định danh giả mạo
        using HttpRequestMessage anonymousRequest = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"guest@example.test\"}", Encoding.UTF8, "application/json")
        };
        anonymousRequest.Headers.Add(GatewayHeaderConstants.UserId, "spoofed-anonymous-user");
        anonymousRequest.Headers.Add(GatewayHeaderConstants.PartyId, "spoofed-anonymous-party");
        anonymousRequest.Headers.Add(GatewayHeaderConstants.Role, "SuperAdmin");

        using HttpResponseMessage response2 = await client.SendAsync(anonymousRequest);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Khi JWT không tồn tại hoặc không xác thực, downstream tuyệt đối KHÔNG nhận bất kỳ identity header nào
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.UserId));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.PartyId));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.Role));
    }

    [Fact]
    public async Task RequestSizeProtectionRejectsPayloadExceedingLimit()
    {
        // Tiêu chí 5: Request Size Protection -> chặn payload vượt quá dung lượng tối đa.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        // Cấu hình giới hạn kích thước là 1 KB (1024 bytes)
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            maxRequestBodySizeBytes: 1024);
        using HttpClient client = factory.CreateClient();

        // Tạo payload có kích thước 2 KB (2048 bytes), vượt quá ngưỡng 1024 bytes
        string oversizedPayload = new('A', 2048);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent(oversizedPayload, Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        GatewayErrorResponse? error = await response.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(GatewayError.PayloadTooLarge.Code, error.Code);
        // Upstream không nhận payload quá khổ này
        Assert.Null(upstream.LastRequest.Method);
    }

    [Fact]
    public async Task SecurityHeadersArePresentOnAllGatewayResponses()
    {
        // Tiêu chí 6: Security Headers chuẩn hóa xuất hiện trên mọi phản hồi từ Gateway.
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/_health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("0", response.Headers.GetValues("X-XSS-Protection").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task NonCrossOriginRequestDoesNotGetRejectedByCorsPolicy()
    {
        // Kịch bản biên: Request trực tiếp không có header Origin (backend call/curl/same-origin) không bị từ chối
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(
            upstream.BaseAddress,
            allowedOrigins: [WhitelistedOrigin]);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"direct@example.test\"}", Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("/api/v1/auth/sessions", upstream.LastRequest.Path);
    }

    [Fact]
    public async Task TokenWithoutPartyIdOrRolesDoesNotForwardThoseHeaders()
    {
        // Kịch bản biên: Token chỉ có sub, không có party_id hoặc role
        // Downstream nhận UserId, nhưng không nhận X-Party-Id hay X-Role (kể cả client cố tình giả mạo)
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        string expectedUserId = Guid.NewGuid().ToString("D");
        string token = CreateToken(
            signingKey,
            keyId: "key-1",
            issuer: Issuer,
            audience: Audience,
            userId: expectedUserId,
            partyId: null,
            roles: null);

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(GatewayHeaderConstants.PartyId, "fake-party-id");
        request.Headers.Add(GatewayHeaderConstants.Role, "fake-role");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedUserId, upstream.LastRequest.Headers[GatewayHeaderConstants.UserId]);
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.PartyId));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.Role));
    }

    [Fact]
    public async Task ValidTokenInCookieForwardsCompleteIdentityContextAndBearerDownstream()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        string expectedUserId = Guid.NewGuid().ToString("D");
        string expectedPartyId = "party-dprental-org-cookie";
        string[] expectedRoles = ["customer"];

        string token = CreateToken(
            signingKey,
            keyId: "key-1",
            issuer: Issuer,
            audience: Audience,
            userId: expectedUserId,
            partyId: expectedPartyId,
            roles: expectedRoles);

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");
        request.Headers.Add("Cookie", $"accessToken={token}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedUserId, upstream.LastRequest.Headers[GatewayHeaderConstants.UserId]);
        Assert.Equal(expectedPartyId, upstream.LastRequest.Headers[GatewayHeaderConstants.PartyId]);
        Assert.Equal("ApiGateway", upstream.LastRequest.Headers[GatewayHeaderConstants.ServiceName]);
        Assert.Equal("customer", upstream.LastRequest.Headers[GatewayHeaderConstants.Role]);
        Assert.Equal($"Bearer {token}", upstream.LastRequest.Headers["Authorization"]);
    }

    [Fact]
    public async Task MediaUploadConvertsCookieToBearerWithoutForwardingCookies()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateToken(signingKey, "key-1", Issuer, Audience, Guid.NewGuid().ToString("D"));

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/media/uploads");
        request.Headers.Add("Cookie", $"accessToken={token}; refreshToken=private-session");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Bearer {token}", upstream.LastRequest.Headers["Authorization"]);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task MediaUploadExplicitBearerTakesPrecedenceOverStaleCookie()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateToken(signingKey, "key-1", Issuer, Audience, Guid.NewGuid().ToString("D"));

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/media/uploads");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("Cookie", "accessToken=invalid-cookie; refreshToken=private-session");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"Bearer {token}", upstream.LastRequest.Headers["Authorization"]);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Cookie"));
    }

    [Theory]
    [InlineData("/api/v1/media/{0}/verify", "GET")]
    [InlineData("/api/v1/media/{0}", "GET")]
    [InlineData("/api/v1/media/{0}/access", "POST")]
    [InlineData("/api/v1/media/{0}/visibility", "PUT")]
    public async Task InternalMediaRoutePassesWithoutBearerAndForwardsApiKey(string routeTemplate, string method)
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        Guid mediaId = Guid.NewGuid();
        string path = string.Format(System.Globalization.CultureInfo.InvariantCulture, routeTemplate, mediaId);
        using HttpRequestMessage request = new(new HttpMethod(method), path);
        request.Headers.Add("X-Api-Key", "sak_test-secret");
        request.Headers.Add("Cookie", "accessToken=stale-user-token");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sak_test-secret", upstream.LastRequest.Headers["X-Api-Key"]);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Authorization"));
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Cookie"));
        Assert.False(upstream.LastRequest.Headers.ContainsKey(GatewayHeaderConstants.UserId));
    }

    [Fact]
    public async Task NonInternalRouteStripsApiKey()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        string token = CreateToken(signingKey, "key-1", Issuer, Audience, "user-123");
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/media/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Api-Key", "stray-key");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task PublicMediaContentAndMediaOpenApiAreAnonymousRoutes()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage content = await client.GetAsync($"/api/v1/media/{Guid.NewGuid():D}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.EndsWith("/content", upstream.LastRequest.Path, StringComparison.Ordinal);

        using HttpRequestMessage swaggerRequest = new(HttpMethod.Get, "/media/swagger/v1/swagger.json");
        swaggerRequest.Headers.Add("Cookie", "accessToken=browser-cookie; refreshToken=private-session");
        using HttpResponseMessage swagger = await client.SendAsync(swaggerRequest);
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Equal("/swagger/v1/swagger.json", upstream.LastRequest.Path);
        Assert.False(upstream.LastRequest.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task MediaUploadUsesItsOwnBodyLimit()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress,
            maxRequestBodySizeBytes: 1024, mediaUploadMaxRequestBodySizeBytes: 4096);
        using HttpClient client = factory.CreateClient();
        string token = CreateToken(signingKey, "key-1", Issuer, Audience, Guid.NewGuid().ToString("D"));

        using HttpRequestMessage upload = new(HttpMethod.Post, "/api/v1/media/uploads")
        {
            Content = new StringContent(new string('A', 2048))
        };
        upload.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage accepted = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpRequestMessage oversized = new(HttpMethod.Post, "/api/v1/media/uploads")
        {
            Content = new StringContent(new string('A', 8192))
        };
        oversized.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage rejected = await client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    [Fact]
    public async Task InvalidTokenInCookieReturns401InvalidToken()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        using RSA untrustedKey = RSA.Create(2048);
        string invalidToken = CreateToken(untrustedKey, "untrusted-key", Issuer, Audience, "fake-user");

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");
        request.Headers.Add("Cookie", $"accessToken={invalidToken}");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingTokenInHeaderAndCookieReturns401AuthenticationRequired()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, includeProtectedRoute: true);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected/orders");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetCookieFromDownstreamIsPreservedAndForwardedToClient()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackSecurityService upstream = new(CreateJwks(signingKey, "key-1"))
        {
            CookieToSet = "accessToken=cookie-token-val; Path=/; HttpOnly; SameSite=Lax"
        };
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Set-Cookie"));
        string setCookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("accessToken=cookie-token-val", setCookie, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Uri upstreamAddress,
        bool includeProtectedRoute = false,
        string[]? allowedOrigins = null,
        long? maxRequestBodySizeBytes = null,
        long? mediaUploadMaxRequestBodySizeBytes = null)
    {
        Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
        {
            ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
            ["Gateway:Authentication:Jwt:Audience"] = Audience,
            ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, ".well-known/jwks.json").AbsoluteUri,
            ["Routes:0:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:0:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:0:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:1:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:1:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:1:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:2:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:2:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:2:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:3:DownstreamScheme"] = upstreamAddress.Scheme,
            ["Routes:3:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host,
            ["Routes:3:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        for (int route = 4; route <= 5; route++)
        {
            overrides[$"Routes:{route}:DownstreamScheme"] = upstreamAddress.Scheme;
            overrides[$"Routes:{route}:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host;
            overrides[$"Routes:{route}:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (allowedOrigins is not null)
        {
            for (int i = 0; i < allowedOrigins.Length; i++)
            {
                overrides[$"Gateway:Security:Cors:AllowedOrigins:{i}"] = allowedOrigins[i];
            }
        }

        if (maxRequestBodySizeBytes.HasValue)
        {
            overrides["Gateway:Security:RequestSize:MaxRequestBodySizeBytes"] = maxRequestBodySizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (mediaUploadMaxRequestBodySizeBytes.HasValue)
        {
            overrides["Gateway:Security:RequestSize:MediaUploadMaxRequestBodySizeBytes"] = mediaUploadMaxRequestBodySizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (includeProtectedRoute)
        {
            overrides["Routes:6:Key"] = "protected-service";
            overrides["Routes:6:UpstreamPathTemplate"] = "/protected/{everything}";
            overrides["Routes:6:DownstreamPathTemplate"] = "/protected/{everything}";
            overrides["Routes:6:DownstreamScheme"] = upstreamAddress.Scheme;
            overrides["Routes:6:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host;
            overrides["Routes:6:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configurationBuilder) => configurationBuilder.AddInMemoryCollection(overrides)));
    }

    private static string CreateToken(
        RSA signingKey,
        string keyId,
        string issuer,
        string audience,
        string userId,
        string? partyId = null,
        IEnumerable<string>? roles = null,
        bool service = false)
    {
        RsaSecurityKey key = new(signingKey)
        {
            KeyId = keyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };

        List<Claim> claims = [new Claim(JwtRegisteredClaimNames.Sub, userId)];

        if (service)
        {
            claims.Add(new Claim("u", "svc"));
            claims.Add(new Claim("s", "media.verify"));
            claims.Add(new Claim("amr", "api_key"));
        }

        if (!string.IsNullOrWhiteSpace(partyId))
        {
            claims.Add(new Claim("party_id", partyId));
        }

        if (roles is not null)
        {
            foreach (string role in roles)
            {
                claims.Add(new Claim("role", role));
            }
        }

        JwtSecurityToken token = new(
            issuer,
            audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateJwks(RSA rsa, string keyId)
    {
        RSAParameters parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    kid = keyId,
                    use = "sig",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                }
            }
        });
    }

    private sealed class LoopbackSecurityService : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private readonly Task acceptingTask;

        internal LoopbackSecurityService(string jwks)
        {
            Jwks = jwks;
            listener.Prefixes.Add($"http://127.0.0.1:{GetAvailablePort()}/");
            listener.Start();
            BaseAddress = new Uri(listener.Prefixes.Single());
            acceptingTask = AcceptRequestsAsync();
        }

        internal Uri BaseAddress { get; }

        internal string Jwks { get; set; }

        internal string? CookieToSet { get; set; }

        internal string? ExchangeToken { get; set; }

        internal string? LastExchangeKey { get; private set; }

        internal Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal CapturedRequest LastRequest { get; private set; } = new();

        public async ValueTask DisposeAsync()
        {
            await cancellationTokenSource.CancelAsync();
            listener.Close();
            try
            {
                await acceptingTask;
            }
            catch (HttpListenerException)
            {
                // Closing HttpListener unblocks GetContextAsync.
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }

        private async Task AcceptRequestsAsync()
        {
            try
            {
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationTokenSource.Token);
                    _ = HandleRequestAsync(context);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the test server is disposed.
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            if (string.Equals(request.Url?.AbsolutePath, "/.well-known/jwks.json", StringComparison.Ordinal))
            {
                await WriteResponseAsync(context.Response, Jwks, "application/json");
                return;
            }

            Dictionary<string, string> capturedHeaders = new(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in request.Headers.AllKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    capturedHeaders[key] = request.Headers[key] ?? string.Empty;
                }
            }

            LastRequest = new CapturedRequest
            {
                Method = request.HttpMethod,
                Path = request.Url?.AbsolutePath,
                Headers = capturedHeaders
            };

            if (request.Url?.AbsolutePath == "/api/v1/auth/api-key/exchange")
            {
                LastExchangeKey = request.Headers["X-Api-Key"];
                await WriteResponseAsync(context.Response, JsonSerializer.Serialize(new
                {
                    accessToken = ExchangeToken,
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
                }), "application/json");
                return;
            }

            if (!string.IsNullOrWhiteSpace(CookieToSet))
            {
                context.Response.Headers.Add("Set-Cookie", CookieToSet);
            }

            foreach ((string name, string value) in ResponseHeaders)
            {
                context.Response.Headers.Add(name, value);
            }

            await WriteResponseAsync(context.Response, "{\"success\":true}", "application/json");
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, string content, string contentType)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static int GetAvailablePort()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }

    private sealed class CapturedRequest
    {
        public string? Method { get; set; }
        public string? Path { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
