using System.Security.Claims;
using Dprental.CatalogService.Api.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class CatalogCallerContextTests
{
    [Fact]
    public void Anonymous_HasNoIdentity()
    {
        CatalogCallerContext caller = new(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(caller.IsAuthenticated);
        Assert.False(caller.IsAdmin);
        Assert.Null(caller.UserId);
        Assert.Empty(caller.Roles);
        Assert.Empty(caller.Permissions);
        Assert.Empty(caller.OrganizationIds);
    }

    [Fact]
    public void User_ReadsAuthWireContractClaims()
    {
        Guid userId = Guid.NewGuid();
        Guid firstOrg = Guid.NewGuid();
        Guid secondOrg = Guid.NewGuid();

        CatalogCallerContext caller = Authenticated(
            ("sub", userId.ToString()),
            ("r", "own"),
            ("r", "mem"),
            ("p", "inventory.asset.manage"),
            ("o", firstOrg.ToString()),
            ("o", secondOrg.ToString()));

        Assert.True(caller.IsAuthenticated);
        Assert.False(caller.IsService);
        Assert.False(caller.IsAdmin);
        Assert.Equal(userId, caller.UserId);
        Assert.Equal(["mem", "own"], caller.Roles.Order());
        Assert.Equal(["inventory.asset.manage"], caller.Permissions);
        Assert.Equal(new HashSet<Guid> { firstOrg, secondOrg }, caller.OrganizationIds);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    [InlineData(ClaimTypes.Role)]
    public void Roles_IgnoreClaimNamesOutsideAuthContract(string claimType)
    {
        CatalogCallerContext caller = Authenticated(("sub", Guid.NewGuid().ToString()), (claimType, "adm"));

        Assert.Empty(caller.Roles);
        Assert.False(caller.IsAdmin);
    }

    [Theory]
    [InlineData("adm", true)]
    [InlineData("ADM", false)]
    [InlineData("Admin", false)]
    [InlineData("adp", false)]
    public void IsAdmin_RequiresExactAdminAlias(string role, bool expected)
    {
        CatalogCallerContext caller = Authenticated(("sub", Guid.NewGuid().ToString()), ("r", role));

        Assert.Equal(expected, caller.IsAdmin);
    }

    [Fact]
    public void ServiceToken_IsNeverAdmin()
    {
        CatalogCallerContext caller = Authenticated(("sub", Guid.NewGuid().ToString()), ("u", "svc"), ("r", "adm"));

        Assert.True(caller.IsService);
        Assert.False(caller.IsAdmin);
    }

    [Fact]
    public void OrganizationIds_SkipMalformedAndEmptyValues()
    {
        Guid organizationId = Guid.NewGuid();
        CatalogCallerContext caller = Authenticated(
            ("sub", Guid.NewGuid().ToString()),
            ("o", organizationId.ToString()),
            ("o", "not-a-guid"),
            ("o", Guid.Empty.ToString()));

        Assert.Equal([organizationId], caller.OrganizationIds);
    }

    [Fact]
    public void IdentityHeaders_AreNeverTrusted()
    {
        DefaultHttpContext httpContext = new() { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        httpContext.Request.Headers["X-User-Id"] = Guid.NewGuid().ToString();
        httpContext.Request.Headers["X-Role"] = "adm";
        httpContext.Request.Headers["X-Organization-Id"] = Guid.NewGuid().ToString();

        CatalogCallerContext caller = new(new HttpContextAccessor { HttpContext = httpContext });

        Assert.False(caller.IsAuthenticated);
        Assert.Null(caller.UserId);
        Assert.Empty(caller.Roles);
        Assert.Empty(caller.OrganizationIds);
    }

    private static CatalogCallerContext Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsPrincipal(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)),
            authenticationType: "Bearer")));
}
