using Confluent.Kafka;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Kafka;

public sealed class KafkaOptionsValidatorTests
{
    private readonly KafkaOptionsValidator _validator = new();

    [Fact]
    public void Disabled_SkipsValidation()
    {
        KafkaOptions options = new()
        {
            Enabled = false,
            BootstrapServers = "",
            ConsumerGroupId = "",
            RentalEventsTopic = "",
            PaymentEventsTopic = ""
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void Enabled_WithCompleteConfiguration_Succeeds()
    {
        Assert.True(_validator.Validate(null, CreateEnabledOptions()).Succeeded);
    }

    [Fact]
    public void Enabled_WithoutBootstrapServers_Fails()
    {
        KafkaOptions options = CreateEnabledOptions();
        options.BootstrapServers = " ";

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("BootstrapServers", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_WithoutConsumerGroupId_Fails()
    {
        KafkaOptions options = CreateEnabledOptions();
        options.ConsumerGroupId = "";

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("ConsumerGroupId", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_WithSameTopicForRentalAndPayment_Fails()
    {
        KafkaOptions options = CreateEnabledOptions();
        options.PaymentEventsTopic = options.RentalEventsTopic;

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("phải khác nhau", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "user", "secret")]
    [InlineData(SaslMechanism.Plain, "", "secret")]
    [InlineData(SaslMechanism.Plain, "user", "")]
    public void Enabled_WithIncompleteSaslCredentials_Fails(SaslMechanism? mechanism, string username, string password)
    {
        KafkaOptions options = CreateEnabledOptions();
        options.SecurityProtocol = SecurityProtocol.SaslSsl;
        options.SaslMechanism = mechanism;
        options.SaslUsername = username;
        options.SaslPassword = password;

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("SASL", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_WithCompleteSaslCredentials_Succeeds()
    {
        KafkaOptions options = CreateEnabledOptions();
        options.SecurityProtocol = SecurityProtocol.SaslPlaintext;
        options.SaslMechanism = SaslMechanism.ScramSha512;
        options.SaslUsername = "analytics";
        options.SaslPassword = "secret";

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void EnabledWithInvalidConfiguration_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Kafka:Enabled", "true");
                builder.UseSetting("Kafka:BootstrapServers", "");
            });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static KafkaOptions CreateEnabledOptions() => new()
    {
        Enabled = true,
        BootstrapServers = "localhost:29092",
        ConsumerGroupId = "analytics-service",
        RentalEventsTopic = "rental.events.v1",
        PaymentEventsTopic = "payment.events.v1"
    };
}
