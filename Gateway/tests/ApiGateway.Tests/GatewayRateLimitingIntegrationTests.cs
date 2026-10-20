using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dprental.Gateway.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;

namespace Dprental.Gateway.Tests;

public sealed class GatewayRateLimitingIntegrationTests
{
    private const string Issuer = "https://auth.example.test";
    private const string HumanAudience = "dprental-clients";
    private const string InventoryAudience = "inventory-api";

    [Fact]
    public async Task GetAndHeadShareReadQuotaAndWriteOperationsShareWriteQuotaIndependently()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
            overrides["Gateway:Security:RateLimiting:UserWriteLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string userId = Guid.NewGuid().ToString("D");
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        // 1. GET 1 -> 200 OK
        using HttpRequestMessage get1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        get1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resGet1 = await client.SendAsync(get1);
        Assert.Equal(HttpStatusCode.OK, resGet1.StatusCode);

        // 2. HEAD 2 -> 200 OK (chia sẻ quota đọc)
        using HttpRequestMessage head2 = new(HttpMethod.Head, "/api/v1/catalog/categories");
        head2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resHead2 = await client.SendAsync(head2);
        Assert.Equal(HttpStatusCode.OK, resHead2.StatusCode);

        // 3. GET 3 -> 429 Too Many Requests (hết quota đọc)
        using HttpRequestMessage get3 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        get3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resGet3 = await client.SendAsync(get3);
        Assert.Equal(HttpStatusCode.TooManyRequests, resGet3.StatusCode);

        // 4. POST 1 -> 200 OK (quota ghi hoàn toàn độc lập, chưa bị ảnh hưởng)
        using HttpRequestMessage post1 = new(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        post1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resPost1 = await client.SendAsync(post1);
        Assert.Equal(HttpStatusCode.OK, resPost1.StatusCode);

        // 5. PUT 2 -> 200 OK (chia sẻ quota ghi)
        using HttpRequestMessage put2 = new(HttpMethod.Put, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        put2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resPut2 = await client.SendAsync(put2);
        Assert.Equal(HttpStatusCode.OK, resPut2.StatusCode);

        // 6. PATCH 3 -> 429 Too Many Requests (hết quota ghi)
        using HttpRequestMessage patch3 = new(HttpMethod.Patch, "/api/v1/auth/sessions")
        {
            Content = new StringContent("{\"email\":\"user@example.test\"}", Encoding.UTF8, "application/json")
        };
        patch3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resPatch3 = await client.SendAsync(patch3);
        Assert.Equal(HttpStatusCode.TooManyRequests, resPatch3.StatusCode);
    }

    [Fact]
    public async Task SameUserChangingIpOrTokenUsesSingleQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "user-fixed-id-1234";
        string token1 = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);
        string token2 = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId); // token khác cùng user

        // Request 1: Token 1 từ IP 1.1.1.1 -> OK
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        req1.Headers.Add("X-Test-Remote-IP", "1.1.1.1");
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Request 2: Token 2 từ IP 2.2.2.2 -> OK
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        req2.Headers.Add("X-Test-Remote-IP", "2.2.2.2");
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // Request 3: Token 1 từ IP 3.3.3.3 -> 429 Too Many Requests (dùng chung quota)
        using HttpRequestMessage req3 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        req3.Headers.Add("X-Test-Remote-IP", "3.3.3.3");
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);
    }

    [Fact]
    public async Task DifferentUsersOnSameIpHaveIndependentQuotas()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string userAlice = "user-alice";
        string userBob = "user-bob";
        string tokenAlice = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userAlice);
        string tokenBob = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userBob);

        // User Alice gửi 2 request từ IP 192.0.2.1 -> Cả 2 thành công
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage reqAlice = new(HttpMethod.Get, "/api/v1/catalog/categories");
            reqAlice.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAlice);
            reqAlice.Headers.Add("X-Test-Remote-IP", "192.0.2.1");
            using HttpResponseMessage resAlice = await client.SendAsync(reqAlice);
            Assert.Equal(HttpStatusCode.OK, resAlice.StatusCode);
        }

        // User Alice gửi request thứ 3 -> 429 Too Many Requests
        using HttpRequestMessage reqAliceBlocked = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqAliceBlocked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenAlice);
        reqAliceBlocked.Headers.Add("X-Test-Remote-IP", "192.0.2.1");
        using HttpResponseMessage resAliceBlocked = await client.SendAsync(reqAliceBlocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, resAliceBlocked.StatusCode);

        // User Bob từ CÙNG IP 192.0.2.1 gửi request -> 200 OK (quota riêng, không bị ảnh hưởng)
        using HttpRequestMessage reqBob = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqBob.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBob);
        reqBob.Headers.Add("X-Test-Remote-IP", "192.0.2.1");
        using HttpResponseMessage resBob = await client.SendAsync(reqBob);
        Assert.Equal(HttpStatusCode.OK, resBob.StatusCode);
    }

    [Fact]
    public async Task AnonymousUsersOnSameIpShareIpQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string sharedIp = "198.51.100.22";

        // Khách 1 trên sharedIp gửi request -> 200 OK
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req1.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Khách 2 trên sharedIp gửi request -> 200 OK
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req2.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // Khách 3 trên sharedIp gửi request -> 429 Too Many Requests (chia sẻ quota IP)
        using HttpRequestMessage req3 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req3.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);

        // Khách trên IP KHÁC (198.51.100.99) gửi request -> 200 OK!
        using HttpRequestMessage reqOtherIp = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqOtherIp.Headers.Add("X-Test-Remote-IP", "198.51.100.99");
        using HttpResponseMessage resOtherIp = await client.SendAsync(reqOtherIp);
        Assert.Equal(HttpStatusCode.OK, resOtherIp.StatusCode);
    }

    [Fact]
    public async Task FakeUserIdHeaderDoesNotCreateUserPartition()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "2";
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "100"; // Nếu ăn theo user thì phải được 100
        });
        using HttpClient client = factory.CreateClient();

        string clientIp = "203.0.113.88";

        // Gửi 2 request kèm header giả mạo X-User-Id
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/catalog/categories");
            req.Headers.Add("X-Test-Remote-IP", clientIp);
            req.Headers.Add("X-User-Id", "fake-admin-uuid");
            req.Headers.Add("userId", "fake-admin-uuid");
            using HttpResponseMessage res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // Request thứ 3 -> Bị chặn với 429 vì vẫn chỉ tính theo IP quota (2), header giả mạo bị bỏ qua
        using HttpRequestMessage reqBlocked = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqBlocked.Headers.Add("X-Test-Remote-IP", clientIp);
        reqBlocked.Headers.Add("X-User-Id", "fake-admin-uuid");
        using HttpResponseMessage resBlocked = await client.SendAsync(reqBlocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, resBlocked.StatusCode);
    }

    [Fact]
    public async Task InvalidOrExpiredTokenOnAuthenticatedRoutePreserves401()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();

        // Route yêu cầu xác thực JWT Bearer (như /api/v1/catalog/manage hoặc route inventory)
        using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/inventory/items");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");

        using HttpResponseMessage response = await client.SendAsync(req);

        // Lỗi xác thực của Ocelot route vẫn giữ nguyên 401 Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CookieJwtAuthenticatesAndUsesUserQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "100";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "cookie-user-123";
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/catalog/categories");
            req.Headers.Add("Cookie", $"accessToken={token}");
            using HttpResponseMessage res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // Request thứ 3 -> 429 Too Many Requests (chạm ngưỡng UserReadLimit = 2)
        using HttpRequestMessage reqBlocked = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqBlocked.Headers.Add("Cookie", $"accessToken={token}");
        using HttpResponseMessage resBlocked = await client.SendAsync(reqBlocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, resBlocked.StatusCode);
    }

    [Fact]
    public async Task InventoryBearerTokenAuthenticatesAndUsesUserQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "inventory-service-sub";
        string token = CreateServiceToken(signingKey, "key-1", Issuer, InventoryAudience, userId);

        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/inventory/stock");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // Request thứ 3 -> 429 Too Many Requests (UserReadLimit = 2 được áp dụng qua scheme InventoryBearer)
        using HttpRequestMessage reqBlocked = new(HttpMethod.Get, "/api/v1/inventory/stock");
        reqBlocked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage resBlocked = await client.SendAsync(reqBlocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, resBlocked.StatusCode);
    }

    [Fact]
    public async Task RouteWithoutUserIdUsesIpQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string sharedIp = "192.0.2.77";

        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/media/11111111-1111-4111-8111-111111111111/content");
        req1.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/media/11111111-1111-4111-8111-111111111111/content");
        req2.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        using HttpRequestMessage req3 = new(HttpMethod.Get, "/api/v1/media/11111111-1111-4111-8111-111111111111/content");
        req3.Headers.Add("X-Test-Remote-IP", sharedIp);
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyForwardedHeaderRespectedWhileSpoofedHeaderIgnored()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "2";
            overrides["Gateway:Security:ForwardedHeaders:KnownProxies:0"] = "10.0.0.1";
        });
        using HttpClient client = factory.CreateClient();

        // 1. Request từ Trusted Proxy 10.0.0.1 với X-Forwarded-For: 203.0.113.10
        // ForwardedHeaders hợp lệ -> Client IP được nhận là 203.0.113.10
        for (int i = 0; i < 2; i++)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, "/api/v1/catalog/categories");
            req.Headers.Add("X-Test-Remote-IP", "10.0.0.1");
            req.Headers.Add("X-Forwarded-For", "203.0.113.10");
            using HttpResponseMessage res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // Request thứ 3 qua Trusted Proxy với cùng X-Forwarded-For -> 429 Too Many Requests
        using HttpRequestMessage reqBlocked = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqBlocked.Headers.Add("X-Test-Remote-IP", "10.0.0.1");
        reqBlocked.Headers.Add("X-Forwarded-For", "203.0.113.10");
        using HttpResponseMessage resBlocked = await client.SendAsync(reqBlocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, resBlocked.StatusCode);

        // 2. Request từ Client trực tiếp 192.168.1.99 (KHÔNG nằm trong KnownProxies)
        // Client cố ý giả mạo X-Forwarded-For: 203.0.113.10
        // Vì Remote IP 192.168.1.99 không phải proxy tin cậy -> ForwardedHeaders bỏ qua header này,
        // IP thật được xác định là 192.168.1.99 -> Có quota riêng -> 200 OK!
        using HttpRequestMessage reqUntrusted = new(HttpMethod.Get, "/api/v1/catalog/categories");
        reqUntrusted.Headers.Add("X-Test-Remote-IP", "192.168.1.99");
        reqUntrusted.Headers.Add("X-Forwarded-For", "203.0.113.10");
        using HttpResponseMessage resUntrusted = await client.SendAsync(reqUntrusted);
        Assert.Equal(HttpStatusCode.OK, resUntrusted.StatusCode);
    }

    [Fact]
    public async Task RateLimitExceededReturns429WithRetryAfterJsonErrorAndNoDownstreamCall()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "1";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "rate-limit-exceeded-user";
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        // Request 1: 200 OK -> Upstream nhận được request
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.NotNull(upstream.LastRequest);

        // Request 2: Vượt quota
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/catalog/categories?query=second");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res2 = await client.SendAsync(req2);

        // Kiểm tra Status Code
        Assert.Equal(HttpStatusCode.TooManyRequests, res2.StatusCode);

        // Kiểm tra Header Retry-After
        Assert.True(res2.Headers.Contains("Retry-After"));
        string retryAfterHeader = res2.Headers.GetValues("Retry-After").Single();
        Assert.True(int.TryParse(retryAfterHeader, out int retryAfterSeconds));
        Assert.True(retryAfterSeconds >= 1);

        // Kiểm tra Payload JSON chuẩn
        GatewayErrorResponse? error = await res2.Content.ReadFromJsonAsync<GatewayErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(GatewayError.RateLimitExceeded.Code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));

        // Kiểm tra upstream không nhận được request thứ hai này
        Assert.DoesNotContain("query=second", upstream.LastRequest.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthChecksAndPreflightOptionsDoNotConsumeQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:IpReadLimit"] = "1";
        });
        using HttpClient client = factory.CreateClient();

        string testIp = "203.0.113.33";

        // Gửi nhiều request health check
        for (int i = 0; i < 5; i++)
        {
            using HttpRequestMessage liveReq = new(HttpMethod.Get, "/health/live");
            liveReq.Headers.Add("X-Test-Remote-IP", testIp);
            using HttpResponseMessage liveRes = await client.SendAsync(liveReq);
            Assert.Equal(HttpStatusCode.OK, liveRes.StatusCode);

            using HttpRequestMessage readyReq = new(HttpMethod.Get, "/_health/ready");
            readyReq.Headers.Add("X-Test-Remote-IP", testIp);
            using HttpResponseMessage readyRes = await client.SendAsync(readyReq);
            Assert.Equal(HttpStatusCode.OK, readyRes.StatusCode);
        }

        // Gửi CORS preflight OPTIONS request
        using HttpRequestMessage preflight = new(HttpMethod.Options, "/api/v1/catalog/categories");
        preflight.Headers.Add("X-Test-Remote-IP", testIp);
        preflight.Headers.Add("Origin", "http://localhost:3000");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        using HttpResponseMessage preflightRes = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightRes.StatusCode);

        // Request GET đầu tiên thực tế vẫn thành công với 200 OK vì health check và preflight không tiêu hao quota
        using HttpRequestMessage actualReq = new(HttpMethod.Get, "/api/v1/catalog/categories");
        actualReq.Headers.Add("X-Test-Remote-IP", testIp);
        using HttpResponseMessage actualRes = await client.SendAsync(actualReq);
        Assert.Equal(HttpStatusCode.OK, actualRes.StatusCode);
    }

    [Fact]
    public async Task ChangingRouteOrQueryStringDoesNotBypassQuota()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "2";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "bypass-attempt-user";
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        // Request 1: Route categories
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Request 2: Cùng route nhưng đổi query string
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/catalog/categories?page=2&limit=50&random=123");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        // Request 3: Đổi hẳn sang route khác (equipment-types) -> 429 Too Many Requests
        using HttpRequestMessage req3 = new(HttpMethod.Get, "/api/v1/catalog/equipment-types");
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, res3.StatusCode);
    }

    [Fact]
    public async Task QuotaRecoversAfterWindowExpires()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            // Cửa sổ 1 giây, 1 đoạn
            overrides["Gateway:Security:RateLimiting:WindowSeconds"] = "1";
            overrides["Gateway:Security:RateLimiting:SegmentsPerWindow"] = "1";
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "1";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "recovery-test-user";
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        // Request 1 -> OK
        using HttpRequestMessage req1 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        // Request 2 ngay lập tức -> 429 Too Many Requests
        using HttpRequestMessage req2 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.TooManyRequests, res2.StatusCode);

        // Chờ cửa sổ trượt hồi phục (> 1.2 giây)
        await Task.Delay(1200);

        // Request 3 sau khi cửa sổ hồi phục -> 200 OK!
        using HttpRequestMessage req3 = new(HttpMethod.Get, "/api/v1/catalog/categories");
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.OK, res3.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRequestsSafelyEnforceLimit()
    {
        using RSA signingKey = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(signingKey, "key-1"));
        using WebApplicationFactory<Program> factory = CreateFactory(upstream.BaseAddress, overrides =>
        {
            overrides["Gateway:Security:RateLimiting:UserReadLimit"] = "5";
        });
        using HttpClient client = factory.CreateClient();

        string userId = "concurrent-test-user";
        string token = CreateToken(signingKey, "key-1", Issuer, HumanAudience, userId);

        const int totalRequests = 10;
        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[totalRequests];

        for (int i = 0; i < totalRequests; i++)
        {
            int index = i;
            tasks[i] = Task.Run(async () =>
            {
                using HttpRequestMessage req = new(HttpMethod.Get, $"/api/v1/catalog/categories?seq={index}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await client.SendAsync(req);
            });
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        int successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        int blockedCount = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(5, successCount);
        Assert.Equal(5, blockedCount);

        foreach (var resp in responses)
        {
            resp.Dispose();
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Uri upstreamAddress,
        Action<Dictionary<string, string?>>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
            });
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                var configuration = configurationBuilder.Build();
                Dictionary<string, string?> overrides = new(StringComparer.Ordinal)
                {
                    ["Gateway:Authentication:Jwt:Issuer"] = Issuer,
                    ["Gateway:Authentication:Jwt:Audience"] = HumanAudience,
                    ["Gateway:Authentication:Jwt:InventoryAudience"] = InventoryAudience,
                    ["Gateway:Authentication:Jwt:JwksUri"] = new Uri(upstreamAddress, "/.well-known/jwks.json").ToString(),
                    ["Gateway:Security:Cors:AllowedOrigins:0"] = "http://localhost:3000"
                };

                foreach (var route in configuration.GetSection("Routes").GetChildren())
                {
                    overrides[$"{route.Path}:DownstreamScheme"] = upstreamAddress.Scheme;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Host"] = upstreamAddress.Host;
                    overrides[$"{route.Path}:DownstreamHostAndPorts:0:Port"] = upstreamAddress.Port.ToString(CultureInfo.InvariantCulture);
                }

                configure?.Invoke(overrides);
                configurationBuilder.AddInMemoryCollection(overrides);
            });
        });
    }

    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-Remote-IP", out var testIp) &&
                        IPAddress.TryParse(testIp, out var ip))
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }

                    await nextMiddleware();
                });

                next(app);
            };
        }
    }
}
