using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Dprental.Gateway.Application.Security;
using Dprental.Gateway.Domain.Security;
using Xunit;

namespace Dprental.Gateway.Tests;

/// <summary>
/// Kiểm thử đơn vị cho bộ trích xuất danh tính IdentityExtractor.
/// Xác minh khả năng xử lý ẩn danh, claim bị thiếu, correlation ID và chuẩn hóa vai trò/quyền hạn.
/// </summary>
public sealed class IdentityExtractorTests
{
    private readonly IdentityExtractor extractor = new();

    [Fact]
    public void ExtractReturnsAnonymousWhenPrincipalIsNull()
    {
        string correlationId = Guid.NewGuid().ToString("D");

        IdentityContext result = extractor.Extract(null, correlationId);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.UserId);
        Assert.Null(result.PartyId);
        Assert.Empty(result.Roles);
        Assert.Empty(result.Permissions);
        Assert.Equal(correlationId, result.CorrelationId);
    }

    [Fact]
    public void ExtractReturnsAnonymousWhenNotAuthenticated()
    {
        ClaimsPrincipal unauthenticated = new(new ClaimsIdentity());
        string correlationId = Guid.NewGuid().ToString("D");

        IdentityContext result = extractor.Extract(unauthenticated, correlationId);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.UserId);
        Assert.Equal(correlationId, result.CorrelationId);
    }

    [Fact]
    public void ExtractReturnsAnonymousWhenUserIdClaimIsMissing()
    {
        List<Claim> claims =
        [
            new Claim("party_id", "party-123"),
            new Claim("role", "customer")
        ];
        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);
        string correlationId = Guid.NewGuid().ToString("D");

        IdentityContext result = extractor.Extract(principal, correlationId);

        Assert.False(result.IsAuthenticated);
        Assert.Null(result.UserId);
        Assert.Equal(correlationId, result.CorrelationId);
    }

    [Fact]
    public void ExtractHandlesNullCorrelationIdWithEmptyStringFallback()
    {
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, "user-1")
        ];
        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);

        IdentityContext authenticatedResult = extractor.Extract(principal, null);
        IdentityContext anonymousResult = extractor.Extract(null, null);

        Assert.True(authenticatedResult.IsAuthenticated);
        Assert.Equal(string.Empty, authenticatedResult.CorrelationId);

        Assert.False(anonymousResult.IsAuthenticated);
        Assert.Equal(string.Empty, anonymousResult.CorrelationId);
    }

    [Fact]
    public void ExtractPopulatesAllFieldsWhenAuthenticatedWithFullClaims()
    {
        string expectedUserId = "user-guid-1234";
        string expectedPartyId = "party-company-5678";
        string correlationId = Guid.NewGuid().ToString("D");

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

        IdentityContext result = extractor.Extract(principal, correlationId);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(expectedUserId, result.UserId);
        Assert.Equal(expectedPartyId, result.PartyId);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Equal(["admin", "operator"], result.Roles);
        Assert.Equal(["read:orders", "write:orders"], result.Permissions);
    }

    [Fact]
    public void ExtractSucceedsWhenOptionalClaimsMissing()
    {
        string expectedUserId = "user-guid-1234";
        string correlationId = Guid.NewGuid().ToString("D");

        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, expectedUserId)
        ];

        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);

        IdentityContext result = extractor.Extract(principal, correlationId);

        Assert.True(result.IsAuthenticated);
        Assert.Equal(expectedUserId, result.UserId);
        Assert.Null(result.PartyId);
        Assert.Equal(correlationId, result.CorrelationId);
        Assert.Empty(result.Roles);
        Assert.Empty(result.Permissions);
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

        IdentityContext result = extractor.Extract(principal, "corr-1");

        Assert.True(result.IsAuthenticated);
        Assert.Equal(["staff", "manager"], result.Roles);
    }

    [Fact]
    public void ExtractTrimsPermissionsAndRemovesDuplicates()
    {
        List<Claim> claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, "user-1"),
            new Claim("permissions", "read:all, write:all "),
            new Claim("permissions", "read:all, delete:all")
        ];

        ClaimsIdentity identity = new(claims, "Bearer");
        ClaimsPrincipal principal = new(identity);

        IdentityContext result = extractor.Extract(principal, "corr-2");

        Assert.True(result.IsAuthenticated);
        Assert.Equal(["read:all", "write:all", "delete:all"], result.Permissions);
    }
}
