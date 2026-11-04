using Xunit;

namespace Dprental.NotificationService.Api.Tests.Fixtures;

[Trait("Category", "Unit")]
public sealed class TestEnvironmentTests
{
    [Fact]
    public void DefaultFallbacks_ReturnExpectedDefaults()
    {
        Assert.Equal("Host=127.0.0.1;Port=20992;Username=notificationservice;Password=postgres;Database=", TestEnvironment.PostgreSqlConnectionString);
        Assert.Equal("127.0.0.1:29092", TestEnvironment.KafkaBootstrapServers);
        Assert.Equal("127.0.0.1:26379", TestEnvironment.RedisConnectionString);
    }

    [Fact]
    public void ResolvePostgresConnectionString_EnvironmentOverrides_TakePrecedence()
    {
        var primaryMap = new Dictionary<string, string?>
        {
            ["TEST_POSTGRES_CONNECTION_STRING"] = "Host=custom-pg;Port=5432;Database=override;",
            ["NOTIFICATION_DB_CONNECTION_STRING"] = "Host=fallback-pg;Port=5432;Database=fallback;"
        };

        var resolvedPrimary = TestEnvironment.ResolvePostgresConnectionString(k => primaryMap.GetValueOrDefault(k));
        Assert.Equal("Host=custom-pg;Port=5432;Database=override;", resolvedPrimary);

        var fallbackMap = new Dictionary<string, string?>
        {
            ["TEST_POSTGRES_CONNECTION_STRING"] = "",
            ["NOTIFICATION_DB_CONNECTION_STRING"] = "Host=fallback-pg;Port=5432;Database=fallback;"
        };

        var resolvedFallback = TestEnvironment.ResolvePostgresConnectionString(k => fallbackMap.GetValueOrDefault(k));
        Assert.Equal("Host=fallback-pg;Port=5432;Database=fallback;", resolvedFallback);

        var emptyMap = new Dictionary<string, string?>();
        var resolvedDefault = TestEnvironment.ResolvePostgresConnectionString(k => emptyMap.GetValueOrDefault(k));
        Assert.Equal(TestEnvironment.DefaultPostgresConnectionString, resolvedDefault);
    }

    [Fact]
    public void ResolveKafkaBootstrapServers_EnvironmentOverrides_TakePrecedence()
    {
        var custom = TestEnvironment.ResolveKafkaBootstrapServers(k => k == "TEST_KAFKA_BOOTSTRAP_SERVERS" ? "custom-kafka:9092" : null);
        Assert.Equal("custom-kafka:9092", custom);

        var fallback = TestEnvironment.ResolveKafkaBootstrapServers(_ => null);
        Assert.Equal(TestEnvironment.DefaultKafkaBootstrapServers, fallback);
    }

    [Fact]
    public void ResolveRedisConnectionString_EnvironmentOverrides_TakePrecedence()
    {
        var custom = TestEnvironment.ResolveRedisConnectionString(k => k == "TEST_REDIS_CONNECTION_STRING" ? "custom-redis:6379" : null);
        Assert.Equal("custom-redis:6379", custom);

        var fallback = TestEnvironment.ResolveRedisConnectionString(_ => null);
        Assert.Equal(TestEnvironment.DefaultRedisConnectionString, fallback);
    }

    [Fact]
    public void GetPostgreSqlConnectionString_FormatsDatabaseNameCorrectly()
    {
        var connStr = TestEnvironment.GetPostgreSqlConnectionString("test_sample_db");
        Assert.Contains("Database=test_sample_db", connStr);
    }

    [Fact]
    public async Task EnsurePostgreSqlAvailableAsync_InvalidPort_ThrowsInvalidOperationExceptionWithDiagnostic()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestEnvironment.EnsurePostgreSqlAvailableAsync("Host=127.0.0.1;Port=59999;Username=invalid;Password=invalid;Timeout=1;"));
        Assert.Contains("PostgreSQL is unreachable", ex.Message);
    }
}
