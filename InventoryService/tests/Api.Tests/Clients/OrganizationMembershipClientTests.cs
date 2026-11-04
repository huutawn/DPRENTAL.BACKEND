using System.Net;
using System.Security.Claims;
using Dprental.InventoryService.Application.Clients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Clients;

public sealed class OrganizationMembershipClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }

    private sealed class AuthenticationFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private static OrganizationMembershipClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler, bool includeToken = true, bool includeInternalKey = true)
    {
        var context = new DefaultHttpContext();
        if (includeToken)
        {
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "test-token" }]);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(), properties, "Bearer");
            context.Features.Set<IAuthenticateResultFeature>(new AuthenticationFeature { AuthenticateResult = AuthenticateResult.Success(ticket) });
        }
        var httpClient = new HttpClient(new Handler(handler)) { BaseAddress = new Uri("http://auth.example/") };
        if (includeInternalKey) httpClient.DefaultRequestHeaders.Add("X-Api-Key", "test-internal-key");
        return new OrganizationMembershipClient(httpClient,
            new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public async Task ReadsMembershipsFromAuthUsingOnlyTheCallerToken()
    {
        var id = Guid.NewGuid();
        var client = CreateClient(request =>
        {
            Assert.Equal("/api/v1/auth/users/organizations", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            Assert.Equal("test-internal-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"[{{\"organizationId\":\"{id:D}\",\"roleName\":\"Owner\",\"type\":1,\"organizationStatus\":\"Active\",\"membershipStatus\":1}}]")
            };
        });
        var result = await client.GetCurrentAsync();
        Assert.True(result.IsSuccess);
        var membership = Assert.Single(result.Value);
        Assert.Equal(id, membership.OrganizationId);
        Assert.Equal("Owner", membership.RoleName);
        Assert.Equal(1, membership.Type);
        Assert.Equal(1, membership.OrganizationStatus);
        Assert.Equal(1, membership.MembershipStatus);
    }

    [Fact]
    public async Task MissingTokenOrInvalidAuthResponseFailsClosed()
    {
        var noToken = CreateClient(_ => throw new InvalidOperationException("Must not call Auth"), includeToken: false);
        Assert.Equal("AUTH_TOKEN_REQUIRED", (await noToken.GetCurrentAsync()).Error.Code);
        var noKey = CreateClient(_ => throw new InvalidOperationException("Must not call Auth"), includeInternalKey: false);
        Assert.Equal("AUTH_SERVICE_NOT_CONFIGURED", (await noKey.GetCurrentAsync()).Error.Code);
        var invalid = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]invalid") });
        Assert.Equal("AUTH_SERVICE_UNAVAILABLE", (await invalid.GetCurrentAsync()).Error.Code);
        var outage = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal("AUTH_SERVICE_UNAVAILABLE", (await outage.GetCurrentAsync()).Error.Code);
        var rejected = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Equal("AUTH_SERVICE_UNAVAILABLE", (await rejected.GetCurrentAsync()).Error.Code);
    }
}
