using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.NotificationService.Api.Security;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Security;

public sealed class NotificationJwksConfigurationManagerTests : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly TestHandler _httpHandler = new();
    private readonly HttpClient _httpClient;
    private readonly NotificationJwtOptions _options;

    public NotificationJwksConfigurationManagerTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        _httpClient = new HttpClient(_httpHandler);
        _options = new NotificationJwtOptions
        {
            Issuer = "https://auth.dprental.vn",
            Audience = "dprental-platform",
            JwksUri = "https://auth.dprental.vn/.well-known/jwks.json",
            TimeoutSeconds = 10,
            RefreshIntervalMinutes = 15,
            MaxStaleMinutes = 60,
            RefreshThrottleSeconds = 30
        };

        _httpHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CreateJwksJson(_rsa, "key-1"), System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task GetConfigurationAsync_FirstCall_FetchesAndCachesJwks()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        var config = await manager.GetConfigurationAsync(CancellationToken.None);

        Assert.NotNull(config);
        Assert.Single(config.SigningKeys);
        Assert.Equal(1, _httpHandler.CallCount);
    }

    [Fact]
    public async Task GetConfigurationAsync_WithinRefreshInterval_UsesCacheWithoutCallingHttp()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        var config1 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(10));

        var config2 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(config1, config2);
        Assert.Equal(1, _httpHandler.CallCount);
    }

    [Fact]
    public async Task GetConfigurationAsync_AfterRefreshInterval_RefreshesFromHttp()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(16));

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _httpHandler.CallCount);
    }

    [Fact]
    public async Task GetConfigurationAsync_AfterRefreshInterval_WhenHttpFails_ReturnsStaleCache()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        var initialConfig = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(20));

        _httpHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var staleConfig = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(initialConfig, staleConfig);
        Assert.Equal(2, _httpHandler.CallCount);
    }

    [Fact]
    public async Task GetConfigurationAsync_StaleCache_AllowedUpTo60Minutes()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        var initialConfig = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(55));

        _httpHandler.Responder = _ => throw new HttpRequestException("JWKS endpoint unreachable");

        var staleConfig = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(initialConfig, staleConfig);
    }

    [Fact]
    public async Task GetConfigurationAsync_StaleCache_ExpiredAfter60Minutes_ThrowsException_FailClosed()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(65));

        _httpHandler.Responder = _ => throw new HttpRequestException("JWKS endpoint unreachable");

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetConfigurationAsync_NoCacheAndHttpFails_ThrowsException()
    {
        _httpHandler.Responder = _ => throw new HttpRequestException("Service down");

        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestRefresh_BeforeRefreshInterval_ForcesImmediateHttpFetch()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        manager.RequestRefresh();

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _httpHandler.CallCount);
    }

    [Fact]
    public async Task RequestRefresh_ThrottledToAtMostOncePer30Seconds()
    {
        using var manager = new NotificationJwksConfigurationManager(_httpClient, _options, _timeProvider);

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _httpHandler.CallCount);

        _timeProvider.Advance(TimeSpan.FromSeconds(40));
        manager.RequestRefresh();
        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _httpHandler.CallCount);

        // Attempt second refresh only 10 seconds later (< 30s throttle)
        _timeProvider.Advance(TimeSpan.FromSeconds(10));
        manager.RequestRefresh();
        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _httpHandler.CallCount); // Still 2, throttled!

        // Advance 25 more seconds (total 35s since last forced refresh > 30s)
        _timeProvider.Advance(TimeSpan.FromSeconds(25));
        manager.RequestRefresh();
        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(3, _httpHandler.CallCount); // Refreshed!
    }

    private static string CreateJwksJson(RSA rsa, string keyId)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = keyId };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        jwk.Use = "sig";
        var jwks = new { keys = new[] { jwk } };
        return JsonSerializer.Serialize(jwks);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _httpClient.Dispose();
        _httpHandler.Dispose();
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Responder(request));
        }
    }
}
