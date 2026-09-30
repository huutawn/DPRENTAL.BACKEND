using System.Net;
using Dprental.Gateway.Authentication;
using Dprental.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.Gateway.Tests;

public sealed class ApiKeyForwardingTests
{
    [Theory]
    [InlineData("GET", "/api/v1/media/11111111-1111-4111-8111-111111111111/verify", true)]
    [InlineData("GET", "/api/v1/media/11111111-1111-4111-8111-111111111111", true)]
    [InlineData("POST", "/api/v1/media/11111111-1111-4111-8111-111111111111/access", true)]
    [InlineData("PUT", "/api/v1/media/11111111-1111-4111-8111-111111111111/visibility", true)]
    [InlineData("POST", "/api/v1/media/uploads", false)]
    [InlineData("GET", "/api/v1/media/11111111-1111-4111-8111-111111111111/content", false)]
    [InlineData("GET", "/api/v1/auth/tokens", false)]
    [InlineData("POST", "/api/v1/auth/login", false)]
    public void ApiKeyRoutePolicyIdentifiesInternalRoutesCorrectly(string method, string path, bool expected)
    {
        var policy = Policy();
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(expected, policy.RequiresApiKey(context.Request));
    }

    [Fact]
    public void InvalidRouteConfigurationFailsAtStartup()
    {
        IConfiguration empty = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => new ApiKeyRoutePolicy(empty));

        IConfiguration badMethod = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:ApiKey:RequiredRoutes:0"] = "BAD /api/v1/media/*"
            }).Build();
        Assert.Throws<InvalidOperationException>(() => new ApiKeyRoutePolicy(badMethod));

        IConfiguration badPattern = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:ApiKey:RequiredRoutes:0"] = "GET /api/v1/media/prefix*suffix"
            }).Build();
        Assert.Throws<InvalidOperationException>(() => new ApiKeyRoutePolicy(badPattern));
    }

    [Theory]
    [InlineData("GET", "/api/v1/media/11111111-1111-4111-8111-111111111111/verify", true)]
    [InlineData("POST", "/api/v1/media/uploads", false)]
    [InlineData("GET", "/api/v1/auth/users", false)]
    public async Task TrustedIdentityDelegatingHandlerForwardsApiKeyOnlyForInternalRoutes(string method, string path, bool shouldForwardKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers["X-Api-Key"] = "test-key-123";

        var contextAccessor = new HttpContextAccessor { HttpContext = context };
        var extractor = new IdentityExtractor();
        var options = Options.Create(new GatewaySecurityOptions());
        var policy = Policy();

        var captureHandler = new CaptureHandler();
        var handler = new TrustedIdentityDelegatingHandler(contextAccessor, extractor, options, policy)
        {
            InnerHandler = captureHandler
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://downstream" + path);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(captureHandler.LastRequest);

        if (shouldForwardKey)
        {
            Assert.True(captureHandler.LastRequest.Headers.Contains("X-Api-Key"));
            Assert.Equal("test-key-123", captureHandler.LastRequest.Headers.GetValues("X-Api-Key").Single());
        }
        else
        {
            Assert.False(captureHandler.LastRequest.Headers.Contains("X-Api-Key"));
        }
    }

    private static ApiKeyRoutePolicy Policy() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Authentication:ApiKey:RequiredRoutes:0"] = "GET /api/v1/media/*/verify",
            ["Gateway:Authentication:ApiKey:RequiredRoutes:1"] = "GET /api/v1/media/*",
            ["Gateway:Authentication:ApiKey:RequiredRoutes:2"] = "POST /api/v1/media/*/access",
            ["Gateway:Authentication:ApiKey:RequiredRoutes:3"] = "PUT /api/v1/media/*/visibility"
        }).Build());

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
