using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Api.Security;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Security;

public sealed class JwksConfigurationManagerTests : IAsyncDisposable
{
    private readonly RSA _signingKey = RSA.Create(2048);
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenerTask;
    private readonly Uri _jwksUri;
    private int _requestCount;
    private bool _simulateDown;

    private sealed class CustomTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    public JwksConfigurationManagerTests()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
        _listener.Start();
        _jwksUri = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/.well-known/jwks.json"));
        _listenerTask = Task.Run(AcceptRequestsAsync);
    }

    [Fact]
    public async Task GetConfigurationAsync_CachesResult_WithinRefreshInterval()
    {
        var fakeTime = new CustomTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient();
        var options = new JwtOptions
        {
            JwksUri = _jwksUri.ToString(),
            CacheDurationMinutes = 5,
            MaxStaleMinutes = 10
        };

        using var manager = new JwksConfigurationManager(client, options, fakeTime);

        // Lần 1: tải từ server
        var config1 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Single(config1.SigningKeys);
        Assert.Equal(1, _requestCount);

        // Lần 2 (ngay sau đó): lấy từ cache
        var config2 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(config1, config2);
        Assert.Equal(1, _requestCount);

        // Tiến thời gian 4 phút (vẫn trong cache duration 5 phút)
        fakeTime.Advance(TimeSpan.FromMinutes(4));
        var config3 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(config1, config3);
        Assert.Equal(1, _requestCount);

        // Tiến thời gian qua 5 phút -> refresh
        fakeTime.Advance(TimeSpan.FromMinutes(2));
        var config4 = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _requestCount);
    }

    [Fact]
    public async Task RequestRefresh_ForcesImmediateReload()
    {
        var fakeTime = new CustomTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient();
        var options = new JwtOptions
        {
            JwksUri = _jwksUri.ToString(),
            CacheDurationMinutes = 5,
            MaxStaleMinutes = 10
        };

        using var manager = new JwksConfigurationManager(client, options, fakeTime);

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _requestCount);

        manager.RequestRefresh();

        await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _requestCount);
    }

    [Fact]
    public async Task Outage_WithinMaxStale_ReturnsCachedConfiguration()
    {
        var fakeTime = new CustomTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient();
        var options = new JwtOptions
        {
            JwksUri = _jwksUri.ToString(),
            CacheDurationMinutes = 5,
            MaxStaleMinutes = 10
        };

        using var manager = new JwksConfigurationManager(client, options, fakeTime);

        var configInitial = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(1, _requestCount);

        // Giả lập AuthService bị down
        _simulateDown = true;

        // Tiến thời gian qua cache duration (6 phút, tức là stale 1 phút, < 10 phút)
        fakeTime.Advance(TimeSpan.FromMinutes(6));

        // Vẫn tái sử dụng cache thành công
        var configStale = await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Same(configInitial, configStale);
    }

    [Fact]
    public async Task Outage_ExceedingMaxStale_ThrowsInvalidOperationException()
    {
        var fakeTime = new CustomTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient();
        var options = new JwtOptions
        {
            JwksUri = _jwksUri.ToString(),
            CacheDurationMinutes = 5,
            MaxStaleMinutes = 10
        };

        using var manager = new JwksConfigurationManager(client, options, fakeTime);

        await manager.GetConfigurationAsync(CancellationToken.None);

        // Giả lập AuthService bị down
        _simulateDown = true;

        // Tiến thời gian qua 11 phút (vượt quá MaxStaleMinutes = 10)
        fakeTime.Advance(TimeSpan.FromMinutes(11));

        // Bắt buộc ném exception, không chấp nhận cache vượt quá giới hạn
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.GetConfigurationAsync(CancellationToken.None));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentUnknownKidsShareOneForcedRefresh()
    {
        using var client = new HttpClient();
        using var manager = new JwksConfigurationManager(client, new JwtOptions { JwksUri = _jwksUri.ToString() });
        await manager.GetConfigurationAsync(CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => manager.EnsureSigningKeyAsync($"unknown-{i}", CancellationToken.None)));
        Assert.All(results, Assert.False);
        Assert.Equal(2, _requestCount);
    }

    [Fact]
    public async Task OutageDoesNotFetchForEveryAuthenticatedRequest()
    {
        var clock = new CustomTimeProvider(DateTimeOffset.UtcNow);
        using var client = new HttpClient();
        using var manager = new JwksConfigurationManager(client, new JwtOptions { JwksUri = _jwksUri.ToString() }, clock);
        await manager.GetConfigurationAsync(CancellationToken.None);
        _simulateDown = true;
        clock.Advance(TimeSpan.FromMinutes(6));
        for (int i = 0; i < 20; i++) await manager.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(2, _requestCount);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ColdOutageHasBoundedFetches()
    {
        _simulateDown = true;
        using var client = new HttpClient();
        using var manager = new JwksConfigurationManager(client, new JwtOptions { JwksUri = _jwksUri.ToString() });
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.GetConfigurationAsync(CancellationToken.None));
        for (int i = 0; i < 10; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetConfigurationAsync(CancellationToken.None));
        Assert.Equal(1, _requestCount);
        Assert.False(manager.HasUsableConfiguration());
    }

    private async Task AcceptRequestsAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                _requestCount++;
                var response = context.Response;

                if (_simulateDown)
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.Close();
                    continue;
                }

                RSAParameters parameters = _signingKey.ExportParameters(false);
                var key = new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "key-test",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent)
                };
                string jwks = JsonSerializer.Serialize(new { keys = new[] { key } });

                byte[] bytes = Encoding.UTF8.GetBytes(jwks);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes);
                response.Close();
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _listenerTask; } catch { }
        _listener.Close();
        _cts.Dispose();
        _signingKey.Dispose();
    }
}
