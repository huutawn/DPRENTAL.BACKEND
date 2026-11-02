using Testcontainers.Kafka;
using Xunit;

namespace Dprental.RecommendationService.Api.Tests.TestHelpers;

public sealed class KafkaTestcontainerFixture : IAsyncLifetime
{
    static KafkaTestcontainerFixture()
    {
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    private readonly KafkaContainer _container = new KafkaBuilder("apache/kafka:4.3.1")
        .Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}
