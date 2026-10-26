using Confluent.Kafka;
using Dprental.AnalyticsService.Api.Tests.TestHelpers;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Ingestion;

public sealed class AnalyticsKafkaOptionsValidatorTests
{
    private readonly AnalyticsKafkaOptionsValidator _validator = new();

    [Fact]
    public void Disabled_SkipsValidation()
    {
        Assert.True(_validator.Validate(null, new AnalyticsKafkaOptions { Enabled = false, BootstrapServers = "" }).Succeeded);
    }

    [Fact]
    public void Enabled_WithCompleteConfiguration_Succeeds()
    {
        using TestProofSigner signer = new();
        Assert.True(_validator.Validate(null, Valid(signer)).Succeeded);
    }

    [Fact]
    public void Enabled_WithoutBootstrapServersOrProofKeys_Fails()
    {
        ValidateOptionsResult result = _validator.Validate(null, new AnalyticsKafkaOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("BootstrapServers", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("ProofPublicKeys", StringComparison.Ordinal));
    }

    [Fact]
    public void Enabled_WithInvalidProofKey_Fails()
    {
        using TestProofSigner signer = new();
        AnalyticsKafkaOptions options = Valid(signer);
        options.ProofPublicKeys["broken"] = "not-base64-key";

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Enabled_WithSameTopicTwice_Fails()
    {
        using TestProofSigner signer = new();
        AnalyticsKafkaOptions options = Valid(signer);
        options.PaymentTopic = options.RentalTopic;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Enabled_WithIncompleteSasl_Fails()
    {
        using TestProofSigner signer = new();
        AnalyticsKafkaOptions options = Valid(signer);
        options.SecurityProtocol = SecurityProtocol.SaslSsl;
        options.SaslMechanism = SaslMechanism.Plain;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void EnabledWithInvalidConfiguration_FailsAtStartup()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("Kafka:Enabled", "true");
            });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static AnalyticsKafkaOptions Valid(TestProofSigner signer) => new()
    {
        Enabled = true,
        BootstrapServers = "localhost:29092",
        ProofPublicKeys = { [TestProofSigner.KeyId] = signer.PublicKeyBase64 }
    };
}
