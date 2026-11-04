using Dprental.NotificationService.Api.Validation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Validation;

[Trait("Category", "Unit")]
public sealed class StartupConfigurationValidatorTests
{
    private static Dictionary<string, string?> CreateValidConfigDict() =>
        new()
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=test;Username=postgres;Password=postgres",
            ["Kafka:BootstrapServers"] = "localhost:9092",
            ["Kafka:Topics"] = "rental.events.v1,payment.analytics.v1",
            ["Services:AuthService:BaseUrl"] = "http://auth-service:8080",
            ["InternalAuthentication:ApiKey"] = "valid-internal-api-key-2026"
        };

    [Fact]
    public void Validate_AllValidConfigurations_SucceedsWithoutException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidConfigDict())
            .Build();

        StartupConfigurationValidator.Validate(config);
    }

    [Fact]
    public void Validate_MissingPostgresConnectionString_ThrowsInvalidOperationException()
    {
        var dict = CreateValidConfigDict();
        dict["ConnectionStrings:Postgres"] = "";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(config));
        Assert.Contains("Postgres", ex.Message);
    }

    [Fact]
    public void Validate_MissingKafkaBootstrapServers_ThrowsInvalidOperationException()
    {
        var dict = CreateValidConfigDict();
        dict["Kafka:BootstrapServers"] = "";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(config));
        Assert.Contains("Kafka:BootstrapServers", ex.Message);
    }

    [Fact]
    public void Validate_MissingKafkaTopics_ThrowsInvalidOperationException()
    {
        var dict = CreateValidConfigDict();
        dict["Kafka:Topics"] = "";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(config));
        Assert.Contains("Kafka:Topics", ex.Message);
    }

    [Fact]
    public void Validate_KafkaTopicsAsList_Succeeds()
    {
        var dict = CreateValidConfigDict();
        dict.Remove("Kafka:Topics");
        dict["Kafka:Topics:0"] = "rental.events.v1";
        dict["Kafka:Topics:1"] = "payment.analytics.v1";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        StartupConfigurationValidator.Validate(config);
    }

    [Fact]
    public void Validate_MissingAuthUrl_ThrowsInvalidOperationException()
    {
        var dict = CreateValidConfigDict();
        dict["Services:AuthService:BaseUrl"] = "";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(config));
        Assert.Contains("AuthService", ex.Message);
    }

    [Fact]
    public void Validate_AuthUrlAlternativeKey_Succeeds()
    {
        var dict = CreateValidConfigDict();
        dict.Remove("Services:AuthService:BaseUrl");
        dict["AuthService:BaseUrl"] = "http://auth-service:8080";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        StartupConfigurationValidator.Validate(config);
    }

    [Fact]
    public void Validate_MissingInternalApiKey_ThrowsInvalidOperationException()
    {
        var dict = CreateValidConfigDict();
        dict["InternalAuthentication:ApiKey"] = "";

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => StartupConfigurationValidator.Validate(config));
        Assert.Contains("InternalAuthentication:ApiKey", ex.Message);
    }
}
