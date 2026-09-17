using Dprental.Gateway.Application.Traffic;
using Dprental.Gateway.Infrastructure.Traffic.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho chính sách cấm tự động thử lại các lệnh nghiệp vụ không an toàn (NoUnsafeRetryPolicy).
/// </summary>
public sealed class NoUnsafeRetryPolicyTests
{
    private readonly NoUnsafeRetryPolicy policy = new(NullLogger<NoUnsafeRetryPolicy>.Instance);

    [Theory]
    [InlineData("POST")]
    [InlineData("post")]
    [InlineData("PATCH")]
    [InlineData("CONNECT")]
    public void IsRetryAllowedReturnsFalseForUnsafeMethodsWhenDisallowed(string unsafeMethod)
    {
        GatewayResilienceOptions options = new() { AllowUnsafeCommandRetries = false };

        bool allowed = policy.IsRetryAllowed(unsafeMethod, options);

        Assert.False(allowed);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void IsRetryAllowedReturnsTrueForIdempotentMethods(string safeMethod)
    {
        GatewayResilienceOptions options = new() { AllowUnsafeCommandRetries = false };

        bool allowed = policy.IsRetryAllowed(safeMethod, options);

        Assert.True(allowed);
    }

    [Fact]
    public void IsRetryAllowedReturnsTrueForPostWhenExplicitlyAllowed()
    {
        GatewayResilienceOptions options = new() { AllowUnsafeCommandRetries = true };

        bool allowed = policy.IsRetryAllowed("POST", options);

        Assert.True(allowed);
    }
}
