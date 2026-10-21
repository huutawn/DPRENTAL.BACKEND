using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.TestHelpers;

public sealed class ValkeyTestcontainerFixture : IAsyncLifetime
{
    static ValkeyTestcontainerFixture()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private readonly RedisContainer _container = new RedisBuilder("valkey/valkey:8.1.10-alpine3.24")
        .Build();
    private ConnectionMultiplexer? _multiplexer;

    public string ConnectionString => _container.GetConnectionString();

    public IConnectionMultiplexer Multiplexer => _multiplexer ?? throw new InvalidOperationException("Valkey fixture not initialized.");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AbortOnConnectFail = false;
        options.AllowAdmin = true;
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        if (_multiplexer != null)
        {
            await _multiplexer.DisposeAsync();
        }
        await _container.DisposeAsync();
    }
}
