using System.Net;
using System.Security.Claims;
using Dprental.AnalyticsService.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Hub;

public sealed class AnalyticsHubSecurityTests
{
    private const string Audience = "dprental-clients";
    private static readonly Guid UserId = Guid.Parse("30000000-0000-0000-0000-00000000000a");

    [Fact]
    public void RealUserWithAudience_IsHubClient() => Assert.True(User(Audience).IsHubClient(Audience));

    [Theory]
    [InlineData("svc")]
    [InlineData("SVC")]
    [InlineData("Svc")]
    public void ServiceToken_CaseInsensitive_IsNotHubClient_NorAdmin(string serviceClaim)
    {
        ClaimsPrincipal service = User(Audience, new Claim("u", serviceClaim), new Claim("r", "adm"));

        Assert.False(service.IsHubClient(Audience));
        Assert.False(service.IsPlatformAdmin());
    }
    [Fact]
    public void WrongAudience_IsNotHubClient() => Assert.False(User("payment-api").IsHubClient(Audience));

    [Fact]
    public void InvalidSub_IsNotHubClient()
    {
        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim("aud", Audience), new Claim("sub", "not-a-guid")], "Bearer"));
        Assert.False(user.IsHubClient(Audience));
    }

    [Theory]
    [InlineData("r", "adm", true)]
    [InlineData("r", "admin_platform", true)]
    [InlineData("role", "adp", true)] // claim "role" như Payment; bản cũ chỉ đọc "r"
    [InlineData("r", "cus", false)]
    public void AdminRole_IsDetected(string claimType, string role, bool expected)
    {
        Assert.Equal(expected, User(Audience, new Claim(claimType, role)).IsPlatformAdmin());
    }

    [Fact]
    public async Task HubNegotiate_WithoutToken_IsUnauthorized()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test"));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/hubs/analytics/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_WithDisallowedOrigin_IsForbidden()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test"));
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/hubs/analytics/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://evil-attacker.com");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HubNegotiate_WithAllowedOrigin_PassesOriginCheckToAuth()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test"));
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Post, "/hubs/analytics/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "http://localhost:3000");
        using HttpResponseMessage response = await client.SendAsync(request);

        // Origin is allowed, so it passes origin guard and hits JWT auth -> 401
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void AnalyticsHubOptions_RejectsEmptyOrWildcard()
    {
        var empty = new Dprental.AnalyticsService.Application.Hub.Options.AnalyticsHubOptions { AllowedOrigins = [] };
        Assert.NotEmpty(empty.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(empty)));

        var wildcard = new Dprental.AnalyticsService.Application.Hub.Options.AnalyticsHubOptions { AllowedOrigins = ["*"] };
        Assert.NotEmpty(wildcard.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(wildcard)));

        var valid = new Dprental.AnalyticsService.Application.Hub.Options.AnalyticsHubOptions { AllowedOrigins = ["http://localhost:3000"] };
        Assert.Empty(valid.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(valid)));
    }
    private static ClaimsPrincipal User(string audience, params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim("aud", audience), new Claim("sub", UserId.ToString()), .. extra], "Bearer"));
}
