using Dprental.AuthService.Api.Security;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class ReturnUrlValidatorTests
{
    private static readonly string[] Allowed = ["http://localhost:3000", "https://app.dprental.example"];

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost:3000/dashboard?tab=1")]
    [InlineData("HTTP://LOCALHOST:3000/x")]
    [InlineData("https://app.dprental.example/home")]
    [InlineData("/")]
    [InlineData("/dashboard")]
    public void IsAllowedAcceptsConfiguredOriginsAndSameOriginPaths(string url)
    {
        Assert.True(ReturnUrlValidator.IsAllowed(url, Allowed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://evil.com")]
    [InlineData("http://localhost:3000.evil.com")]
    [InlineData("http://localhost:3001")]
    [InlineData("https://localhost:3000")]
    [InlineData("http://localhost:3000@evil.com")]
    [InlineData("http://evil.com@localhost:3000")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://localhost:3000")]
    [InlineData("dashboard")]
    public void IsAllowedRejectsUnknownOriginsAndRedirectTricks(string? url)
    {
        Assert.False(ReturnUrlValidator.IsAllowed(url, Allowed));
    }

    [Fact]
    public void IsAllowedRejectsAbsoluteUrlsWhenNoOriginIsConfigured()
    {
        Assert.False(ReturnUrlValidator.IsAllowed("http://localhost:3000", []));
        Assert.True(ReturnUrlValidator.IsAllowed("/dashboard", []));
    }
}
