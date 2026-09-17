using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ trích xuất danh tính IdentityExtractor.
/// </summary>
public sealed class IdentityExtractorTests
{
    private readonly IdentityExtractor extractor = new();

    [Fact]
    public void ExtractReturnsAnonymousWhenPrincipalIsNull()
    {
        DefaultHttpContext httpContext = new();

        IdentityContext result = extractor.Extract(null, httpContext);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.UserId);
    }

    [Fact]
    public void ExtractReturnsAnonymousWhenNotAuthenticated()
    {
        ClaimsPrincipal unauthenticated = new(new ClaimsIdentity());
        DefaultHttpContext httpContext = new();

        IdentityContext result = extractor.Extract(unauthenticated, httpContext);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.UserId);
    }

    [Fact]
    public void ExtractPopulatesAllFieldsWhenAuthenticatedWithFullClaims()
    {
        string expectedUserId = "user-guid-1234";
        string expectedPartyId = "party-company-5678";

        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, expectedUserId),
            new Claim("party_id", expectedPartyId),
            new Claim("role", "admin"),
            new Claim("role", "operator"),
            new Claim("permissions", "read:orders, write:orders")
        ];

        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);

        DefaultHttpContext httpContext = new();
        string correlationId = Guid.NewGuid().ToString("D");
        httpContext.TraceIdentifier = correlationId;

        IdentityContext result = extractor.Extract(principal, httpContext);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(expectedUserId, result.UserId);
        Assert.Equal(expectedPartyId, result.PartyId);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Equal(["admin", "operator"], result.Roles);
        Assert.Equal(["read:orders", "write:orders"], result.Permissions);
    }

    [Fact]
    public void ExtractTrimsRolesAndRemovesDuplicates()
    {
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, "user-1"),
            new Claim("role", " staff "),
            new Claim("role", "staff"),
            new Claim("role", "manager")
        ];

        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);
        DefaultHttpContext httpContext = new();

        IdentityContext result = extractor.Extract(principal, httpContext);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(["staff", "manager"], result.Roles);
    }
}
