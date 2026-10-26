using System.Security.Claims;
using Dprental.AnalyticsService.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests;

public sealed class DashboardAdminPolicyTests
{
    [Theory]
    [InlineData("adm", null, true)]
    [InlineData("adp", null, true)]
    [InlineData("cus", null, false)]
    [InlineData("adm", "svc", false)]
    public async Task Policy_OnlyAllowsHumanAdmin(string role, string? tokenUse, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnalyticsSecurity(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var claims = new List<Claim> { new("r", role) };
        if (tokenUse is not null) claims.Add(new Claim("u", tokenUse));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var auth = provider.GetRequiredService<IAuthorizationService>();

        var result = await auth.AuthorizeAsync(user, null, AnalyticsJwtAuthentication.AdminPolicy);
        Assert.Equal(allowed, result.Succeeded);
    }
}
