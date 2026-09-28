using Dprental.Gateway.Application.Traffic;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ thẩm định cấu hình GatewayTrafficOptionsValidator.
/// </summary>
public sealed class GatewayTrafficOptionsValidatorTests
{
    private readonly GatewayTrafficOptionsValidator validator = new();

    [Fact]
    public void ValidOptionsPassesValidation()
    {
        GatewayTrafficOptions options = new()
        {
            RateLimiting = new GatewayRateLimitingOptions
            {
                Anonymous = new RateLimitTierOptions { PermitLimit = 20, WindowSeconds = 60 },
                Authenticated = new RateLimitTierOptions { PermitLimit = 100, WindowSeconds = 60 },
                Sensitive = new RateLimitTierOptions { PermitLimit = 5, WindowSeconds = 60 }
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPermitLimitFailsValidation(int invalidLimit)
    {
        GatewayTrafficOptions options = new()
        {
            RateLimiting = new GatewayRateLimitingOptions
            {
                Anonymous = new RateLimitTierOptions { PermitLimit = invalidLimit, WindowSeconds = 60 }
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("PermitLimit", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidWindowSecondsFailsValidation(int invalidWindow)
    {
        GatewayTrafficOptions options = new()
        {
            RateLimiting = new GatewayRateLimitingOptions
            {
                Anonymous = new RateLimitTierOptions { PermitLimit = 10, WindowSeconds = invalidWindow }
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("WindowSeconds", result.FailureMessage);
    }

    [Fact]
    public void NegativeQueueLimitFailsValidation()
    {
        GatewayTrafficOptions options = new()
        {
            RateLimiting = new GatewayRateLimitingOptions
            {
                Anonymous = new RateLimitTierOptions { PermitLimit = 10, WindowSeconds = 60, QueueLimit = -1 }
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("QueueLimit", result.FailureMessage);
    }
}
