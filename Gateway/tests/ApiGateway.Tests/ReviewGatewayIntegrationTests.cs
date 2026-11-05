using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Dprental.Gateway.Models.Security;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using static Dprental.Gateway.Tests.DownstreamGatewayTestHost;

namespace Dprental.Gateway.Tests;

public sealed class ReviewGatewayIntegrationTests
{
    [Theory]
    [InlineData("/api/v1/reviews/listings/11111111-1111-1111-1111-111111111111?page=2")]
    [InlineData("/api/v1/reviews/suppliers/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/v1/reviews/summary?listingIds=11111111-1111-1111-1111-111111111111")]
    public async Task PublicReadsPreservePathAndQuery(string path)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, upstream.LastRequest?.Path);
    }

    [Theory]
    [InlineData("POST", "/api/v1/reviews")]
    [InlineData("PUT", "/api/v1/reviews/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/v1/reviews/summary")]
    [InlineData("GET", "/api/v1/reviews/my/received")]
    [InlineData("GET", "/api/v1/reviews/my/reputation")]
    [InlineData("GET", "/api/v1/admin/reviews/reports")]
    [InlineData("POST", "/api/v1/admin/reviews/11111111-1111-1111-1111-111111111111/hide")]
    public async Task ProtectedRoutesRejectAnonymousAndInvalidAudience(string method, string path)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        using var anonymous = new HttpRequestMessage(new HttpMethod(method), path);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var invalid = new HttpRequestMessage(new HttpMethod(method), path);
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateToken(key, "key-1", "https://auth.example.test", "wrong-audience", Guid.NewGuid().ToString()));
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Null(upstream.LastRequest);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    [InlineData(412)]
    public async Task ForwardsBearerConcurrencyHeadersAndDownstreamResult(int status)
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1")) { ResponseStatusCode = status };
        upstream.ResponseHeaders["ETag"] = "\"2\"";
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        string token = CreateToken(key, "key-1", "https://auth.example.test", "dprental-clients", Guid.NewGuid().ToString());
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/reviews/11111111-1111-1111-1111-111111111111")
        { Content = new StringContent("{\"rating\":5}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("If-Match", "\"1\"");
        request.Headers.Add("Idempotency-Key", "review-edit-1");
        request.Headers.Add(GatewayHeaderConstants.CorrelationId, "11111111-1111-1111-1111-111111111111");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        Assert.Equal($"Bearer {token}", upstream.LastRequest?.Authorization);
        Assert.Equal("\"1\"", upstream.LastRequest?.Headers["If-Match"]);
        Assert.Equal("review-edit-1", upstream.LastRequest?.Headers["Idempotency-Key"]);
        Assert.Equal("11111111-1111-1111-1111-111111111111", upstream.LastRequest?.Headers[GatewayHeaderConstants.CorrelationId]);
        Assert.Equal("{\"rating\":5}", upstream.LastRequest?.Body);
    }

    [Fact]
    public async Task BrowserCanSendIfMatchAndReadEtag()
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Gateway:Security:Cors:AllowedOrigins:0"] = "https://review.example.test" })));
        using HttpClient client = factory.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/reviews/11111111-1111-1111-1111-111111111111");
        preflight.Headers.Add("Origin", "https://review.example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,if-match,idempotency-key");
        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Contains("If-Match", string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reviews/summary");
        read.Headers.Add("Origin", "https://review.example.test");
        using var response = await client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ETag", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InternalReviewRoutesNeverReachDownstream()
    {
        using RSA key = RSA.Create(2048);
        await using LoopbackTestService upstream = new(CreateJwks(key, "key-1"));
        using var factory = CreateFactory(upstream.BaseAddress);
        using HttpClient client = factory.CreateClient();
        using var response = await client.GetAsync("/internal/reviews/test");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.Null(upstream.LastRequest);
    }
}
