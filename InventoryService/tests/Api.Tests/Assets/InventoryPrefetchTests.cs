using System.Security.Claims;
using Dprental.InventoryService.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class InventoryPrefetchTests
{
    [Theory]
    [InlineData("Purpose", "navigate", "Sec-Purpose", "prefetch", true)]
    [InlineData("Sec-Purpose", "prefetch;prerender", "X-Purpose", "navigate", true)]
    [InlineData("X-Purpose", "navigate, PREFETCH\tprerender", "Purpose", "", true)]
    [InlineData("Purpose", "notprefetch", "Sec-Purpose", "x-prefetch", false)]
    public void AnyHeaderTokenMayIdentifyPrefetch(string first, string value, string second, string other, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[first] = value;
        context.Request.Headers[second] = new StringValues(new[] { "navigate", other });
        var caller = new InventoryCallerContext(new HttpContextAccessor { HttpContext = context });
        Assert.Equal(expected, caller.IsPrefetch);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ServiceClaimMustBeAuthenticated(bool authenticated, bool expected)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("u", "svc") }, authenticated ? "test" : null);
        var caller = new InventoryCallerContext(new ClaimsPrincipal(identity), hasValidApiKey: true);
        Assert.Equal(expected, caller.HasServiceTokenClaim);
    }
}
