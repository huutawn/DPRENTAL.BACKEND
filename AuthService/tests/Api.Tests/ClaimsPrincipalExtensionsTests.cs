using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Common.Enums;
using Xunit;

namespace Dprental.AuthService.Api.Tests;

public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void GetJwtSubReturnsUserIdForValidAuthenticatedToken()
    {
        Guid userId = Guid.NewGuid();
        ClaimsPrincipal principal = CreatePrincipal([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D"))]);

        Guid? result = principal.GetJwtSub();

        Assert.Equal(userId, result);
    }

    [Fact]
    public void GetJwtSubReturnsNullWhenSubClaimIsMissing()
    {
        ClaimsPrincipal principal = CreatePrincipal([]);

        Assert.Null(principal.GetJwtSub());
    }

    [Fact]
    public void GetJwtSubReturnsNullWhenSubIsNotAGuid()
    {
        ClaimsPrincipal principal = CreatePrincipal([new Claim(JwtRegisteredClaimNames.Sub, "not-a-guid")]);

        Assert.Null(principal.GetJwtSub());
    }

    [Fact]
    public void GetJwtSubReturnsNullWhenSubIsEmptyGuid()
    {
        ClaimsPrincipal principal = CreatePrincipal([new Claim(JwtRegisteredClaimNames.Sub, Guid.Empty.ToString("D"))]);

        Assert.Null(principal.GetJwtSub());
    }

    [Fact]
    public void GetJwtSubReturnsNullWhenIdentityIsNotAuthenticated()
    {
        Guid userId = Guid.NewGuid();
        ClaimsIdentity identity = new([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString("D"))]); // không truyền authenticationType => IsAuthenticated = false
        ClaimsPrincipal principal = new(identity);

        Assert.Null(principal.GetJwtSub());
    }

    [Fact]
    public void GetJwtSubReturnsNullForServiceToken()
    {
        Guid servicePrincipalId = Guid.NewGuid();
        ClaimsPrincipal principal = CreatePrincipal([
            new Claim(JwtRegisteredClaimNames.Sub, servicePrincipalId.ToString("D")),
            new Claim(JwtClaimCatalog.Claims.TokenUse, JwtClaimCatalog.TokenUses.Service)
        ]);

        // sub của service token cũng là một GUID hợp lệ (ServicePrincipal.Id) — phải bị từ chối vì không phải user.
        Assert.Null(principal.GetJwtSub());
    }

    [Fact]
    public void GetJwtSubThrowsForNullPrincipal()
    {
        ClaimsPrincipal? principal = null;

        Assert.Throws<ArgumentNullException>(() => principal!.GetJwtSub());
    }

    private static ClaimsPrincipal CreatePrincipal(IEnumerable<Claim> claims)
    {
        ClaimsIdentity identity = new(claims, authenticationType: "Bearer");
        return new ClaimsPrincipal(identity);
    }
}
