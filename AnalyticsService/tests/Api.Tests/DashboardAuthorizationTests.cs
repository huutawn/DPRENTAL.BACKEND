using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardAuthorizationTests
{
    [Theory]
    [InlineData("overview")]
    [InlineData("revenue")]
    [InlineData("orders")]
    [InlineData("equipment")]
    [InlineData("suppliers")]
    [InlineData("me/overview")]
    [InlineData("me/revenue")]
    public async Task DashboardRoute_RejectsAnonymousCaller(string route)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/v1/analytics/{route}?from=2026-10-01&to=2026-10-02");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
