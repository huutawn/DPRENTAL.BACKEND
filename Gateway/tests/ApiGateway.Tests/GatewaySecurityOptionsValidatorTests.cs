using Dprental.Gateway.Security;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ thẩm định cấu hình bảo mật GatewaySecurityOptionsValidator.
/// </summary>
public sealed class GatewaySecurityOptionsValidatorTests
{
    private readonly GatewaySecurityOptionsValidator validator = new();

    [Fact]
    public void ValidOptionsPassesValidation()
    {
        GatewaySecurityOptions options = new()
        {
            Cors = new GatewayCorsOptions
            {
                AllowedOrigins = ["https://app.dprental.vn", "http://localhost:3000"],
                PreflightMaxAgeSeconds = 3600,
                AllowCredentials = true
            },
            RequestSize = new GatewayRequestSizeOptions
            {
                MaxRequestBodySizeBytes = 10485760
            },
            Headers = new GatewaySecurityHeadersOptions
            {
                HstsMaxAgeSeconds = 31536000
            },
            Identity = new GatewayIdentityOptions
            {
                ServiceName = "ApiGateway"
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
    public void InvalidMaxRequestBodySizeBytesFailsValidation(long invalidSize)
    {
        GatewaySecurityOptions options = new()
        {
            RequestSize = new GatewayRequestSizeOptions { MaxRequestBodySizeBytes = invalidSize }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("MaxRequestBodySizeBytes", result.FailureMessage);
    }

    [Fact]
    public void InvalidOriginUriFailsValidation()
    {
        GatewaySecurityOptions options = new()
        {
            Cors = new GatewayCorsOptions { AllowedOrigins = ["not-a-valid-uri"] }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("không phải là một URI hợp lệ", result.FailureMessage);
    }

    [Fact]
    public void AllowCredentialsWithWildcardOriginFailsValidation()
    {
        GatewaySecurityOptions options = new()
        {
            Cors = new GatewayCorsOptions
            {
                AllowCredentials = true,
                AllowedOrigins = ["*"]
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AllowCredentials=true với AllowedOrigins='*'", result.FailureMessage);
    }

    [Fact]
    public void EmptyServiceNameFailsValidation()
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "" }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ServiceName", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidWindowSecondsFailsValidation(int invalidWindow)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { WindowSeconds = invalidWindow }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("WindowSeconds", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void InvalidSegmentsPerWindowFailsValidation(int invalidSegments)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { SegmentsPerWindow = invalidSegments }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("SegmentsPerWindow", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void InvalidUserReadLimitFailsValidation(int invalidLimit)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { UserReadLimit = invalidLimit }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("UserReadLimit", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidUserWriteLimitFailsValidation(int invalidLimit)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { UserWriteLimit = invalidLimit }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("UserWriteLimit", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidIpReadLimitFailsValidation(int invalidLimit)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { IpReadLimit = invalidLimit }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("IpReadLimit", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidIpWriteLimitFailsValidation(int invalidLimit)
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions { IpWriteLimit = invalidLimit }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("IpWriteLimit", result.FailureMessage);
    }

    [Fact]
    public void WindowSecondsNotDivisibleBySegmentsFailsValidation()
    {
        GatewaySecurityOptions options = new()
        {
            Identity = new GatewayIdentityOptions { ServiceName = "ApiGateway" },
            RateLimiting = new GatewayRateLimitingOptions
            {
                WindowSeconds = 60,
                SegmentsPerWindow = 7 // 60 % 7 != 0
            }
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("phải chia hết cho SegmentsPerWindow", result.FailureMessage);
    }
}
