using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.Security.Jwt;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PaymentJwksConfigurationManagerTests
{
    private static string CreateValidJwksJson()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        var jwk = new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = "test-kid-1",
                    alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!)
                }
            }
        };
        return JsonSerializer.Serialize(jwk);
    }

    private static PaymentJwtOptions CreateOptions(int maxStaleMinutes = 60) => new()
    {
        Issuer = "dprental-authservice-dev",
        ServiceAudience = "payment-api",
        ClientAudience = "dprental-clients",
        JwksUri = "http://localhost/jwks.json",
        TimeoutSeconds = 5,
        MaxStaleMinutes = maxStaleMinutes
    };

    [Fact]
    public async Task GetConfigurationAsync_NetworkFailureBeforeAndAfterMaxStale_AndDuringRetryWindow_EnforcesMaxStaleMinutes()
    {
        string jwksJson = CreateValidJwksJson();
        bool shouldFail = false;
        int requestCount = 0;

        using var handler = new StubHttpMessageHandler((req, ct) =>
        {
            requestCount++;
            if (shouldFail)
            {
                throw new HttpRequestException("Simulated JWKS network outage");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwksJson, Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler);
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        using var manager = new PaymentJwksConfigurationManager(httpClient, CreateOptions(maxStaleMinutes: 60), fakeTime);

        // 1. Initial fetch at T = 0 succeeds
        var config1 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Single(config1.SigningKeys);
        Assert.Equal(1, requestCount);

        // 2. Advance to T + 59m55s (past RefreshAfterUtc = 30m, but 5s before MaxStaleMinutes = 60m)
        fakeTime.Advance(TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(55)));
        shouldFail = true;

        // Outage just before MaxStaleMinutes falls back to cache and sets _retryAfterUtc = T + 60m10s
        var fallbackConfig = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(config1, fallbackConfig);
        Assert.Equal(2, requestCount);

        // 3. Advance 3s to T + 59m58s (inside retry window AND still <= MaxStaleMinutes): returns cache without HTTP call
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        var retryWindowValid = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(config1, retryWindowValid);
        Assert.Equal(2, requestCount);

        // 4. Advance 5s to T + 60m03s (STILL inside _retryAfterUtc = 60m10s, but now > MaxStaleMinutes = 60m!)
        // Must NOT return stale cache; must attempt fetch and throw HttpRequestException
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
        Assert.Equal(3, requestCount);
    }

    [Fact]
    public async Task GetConfigurationAsync_CallerCancellation_PropagatesOperationCanceledException_EvenWhenCacheIsValid()
    {
        string jwksJson = CreateValidJwksJson();
        using var cts = new CancellationTokenSource();

        using var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            if (cts.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }

            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwksJson, Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler);
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        using var manager = new PaymentJwksConfigurationManager(httpClient, CreateOptions(maxStaleMinutes: 60), fakeTime);

        // 1. Populate valid cache at T = 0
        var initial = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.NotNull(initial);

        // 2. Advance to T + 35m (past RefreshAfterUtc = 30m, but well within MaxStaleMinutes = 60m)
        fakeTime.Advance(TimeSpan.FromMinutes(35));

        // 3. Cancel caller token and verify OperationCanceledException propagates instead of falling back to cache
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetConfigurationAsync(cts.Token));
    }

    [Fact]
    public async Task GetConfigurationAsync_CallerCancelledDuringInFlightHttp_PropagatesOperationCanceledException()
    {
        string jwksJson = CreateValidJwksJson();
        using var cts = new CancellationTokenSource();
        bool cancelInFlight = false;

        using var handler = new StubHttpMessageHandler((req, ct) =>
        {
            if (cancelInFlight)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jwksJson, Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler);
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero));
        using var manager = new PaymentJwksConfigurationManager(httpClient, CreateOptions(maxStaleMinutes: 60), fakeTime);

        // 1. Populate valid cache at T = 0
        await manager.GetConfigurationAsync(CancellationToken.None);

        // 2. Advance past RefreshAfterUtc (30m) while still within MaxStaleMinutes (60m)
        fakeTime.Advance(TimeSpan.FromMinutes(31));
        cancelInFlight = true;

        // 3. Caller cancellation during in-flight HTTP request must propagate
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetConfigurationAsync(cts.Token));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
