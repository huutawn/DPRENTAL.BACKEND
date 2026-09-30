using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.Gateway.Security;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class IdentityExtractorTests
{
    private readonly IdentityExtractor extractor = new();

    [Fact]
    public void MissingOrUnauthenticatedPrincipalIsAnonymous()
    {
        Assert.Equal(IdentityContext.Anonymous, extractor.Extract(null));
        Assert.Equal(IdentityContext.Anonymous, extractor.Extract(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void AuthenticatedPrincipalWithoutUserIdIsAnonymous()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim("role", "admin")], "Bearer"));

        Assert.Equal(IdentityContext.Anonymous, extractor.Extract(principal));
    }

    [Fact]
    public void AuthenticatedPrincipalForwardsOnlyRequiredIdentityFields()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "user-1"),
            new Claim("party_id", "party-1"),
            new Claim("role", "admin, operator"),
            new Claim("role", "admin"),
            new Claim("permissions", "orders:write")
        ], "Bearer"));

        IdentityContext result = extractor.Extract(principal);

        Assert.True(result.IsAuthenticated);
        Assert.Equal("user-1", result.UserId);
        Assert.Equal("party-1", result.PartyId);
        Assert.Equal(["admin", "operator"], result.Roles);
    }

    [Fact]
    public void OptionalClaimsMayBeMissing()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, "user-1")], "Bearer"));

        IdentityContext result = extractor.Extract(principal);

        Assert.True(result.IsAuthenticated);
        Assert.Equal("user-1", result.UserId);
        Assert.Null(result.PartyId);
        Assert.Empty(result.Roles);
    }

    [Fact]
    public void TrimsRolesAndRemovesDuplicates()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "user-1"),
            new Claim("role", " staff "),
            new Claim("role", "staff, manager")
        ], "Bearer"));

        Assert.Equal(["staff", "manager"], extractor.Extract(principal).Roles);
    }
}
