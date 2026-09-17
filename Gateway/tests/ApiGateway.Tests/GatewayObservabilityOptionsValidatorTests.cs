using Dprental.Gateway.Application.Observability;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ thẩm định cấu hình GatewayObservabilityOptionsValidator.
/// </summary>
public sealed class GatewayObservabilityOptionsValidatorTests
{
    private readonly GatewayObservabilityOptionsValidator validator = new();

    [Fact]
    public void ValidOptionsPassesValidation()
    {
        GatewayObservabilityOptions options = new()
        {
            Logging = new GatewayLoggingOptions
            {
                Enabled = true,
                IncludeQueryString = false,
                RedactedHeaderNames = ["Authorization", "Cookie"]
            },
            Metrics = new GatewayMetricsOptions
            {
                Enabled = true
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NullOptionsFailsValidation()
    {
        ValidateOptionsResult result = validator.Validate(null, null);

        Assert.True(result.Failed);
        Assert.Contains("không được để trống", result.FailureMessage);
    }

    [Fact]
    public void EmptyRedactedHeadersFailsValidation()
    {
        GatewayObservabilityOptions options = new()
        {
            Logging = new GatewayLoggingOptions
            {
                RedactedHeaderNames = []
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("RedactedHeaderNames", result.FailureMessage);
    }
}
