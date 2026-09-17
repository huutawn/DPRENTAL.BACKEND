using Dprental.Gateway.Observability;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class TelemetryOptionsValidatorTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void ValidateRejectsSamplingRatioOutsideClosedUnitInterval(double samplingRatio)
    {
        TelemetryOptions options = new()
        {
            Tracing = new TracingOptions { SamplingRatio = samplingRatio }
        };

        Assert.True(new TelemetryOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("collector:4318/v1/traces")]
    [InlineData("https://collector.example.test/traces")]
    public void ValidateRejectsInvalidOtlpTracesEndpoint(string endpoint)
    {
        TelemetryOptions options = new()
        {
            Otlp = new OtlpOptions { TracesEndpoint = endpoint }
        };

        Assert.True(new TelemetryOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void ValidateAcceptsOptionalOrCompleteOtlpTracesEndpoint()
    {
        TelemetryOptions disabledExporter = new();
        TelemetryOptions configuredExporter = new()
        {
            Otlp = new OtlpOptions { TracesEndpoint = "https://collector.example.test/v1/traces" }
        };

        Assert.False(new TelemetryOptionsValidator().Validate(null, disabledExporter).Failed);
        Assert.False(new TelemetryOptionsValidator().Validate(null, configuredExporter).Failed);
    }
}
