using System.Net;
using System.Security.Claims;
using Dprental.CatalogService.Api.Clients;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class InventoryServiceClientTests
{
    #region InventoryServiceClient Strict Contract and Auth Tests

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handlerFunc(request, cancellationToken);
    }

    [Fact]
    public async Task InventoryServiceClient_PublicScope_DoesNotSendToken()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingMockHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}", System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer user-token";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("public", null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task InventoryServiceClient_MineScope_SendsBearerTokenFromHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingMockHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}", System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CatalogClaims.Types.Subject, Guid.NewGuid().ToString())], "Bearer"));
        httpContext.Request.Headers.Authorization = "Bearer my-header-token";
        SaveAuthenticatedToken(httpContext, "my-header-token");
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("mine", "Active");

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("my-header-token", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Contains("status=Active", capturedRequest.RequestUri?.Query);
    }

    [Fact]
    public async Task InventoryServiceClient_MineScope_SendsBearerTokenFromCookie_WhenHeaderAbsent()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingMockHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}", System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CatalogClaims.Types.Subject, Guid.NewGuid().ToString())], "Bearer"));
        httpContext.Request.Headers.Cookie = "accessToken=my-cookie-token";
        SaveAuthenticatedToken(httpContext, "my-cookie-token");
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("mine", null);

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedRequest);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("my-cookie-token", capturedRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task InventoryServiceClient_MineScope_PrefersHeaderOverCookie()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingMockHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}", System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CatalogClaims.Types.Subject, Guid.NewGuid().ToString())], "Bearer"));
        httpContext.Request.Headers.Authorization = "Bearer header-token";
        SaveAuthenticatedToken(httpContext, "header-token");
        httpContext.Request.Headers.Cookie = "accessToken=cookie-token";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("mine", null);

        Assert.True(result.IsSuccess);
        Assert.Equal("header-token", capturedRequest?.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task InventoryServiceClient_MineScope_ServicePrincipal_Returns403()
    {
        var httpClient = new HttpClient(new DelegatingMockHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            BaseAddress = new Uri("http://localhost:5003/")
        };
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(CatalogClaims.Types.Subject, Guid.NewGuid().ToString()),
                new Claim(CatalogClaims.Types.TokenUse, CatalogClaims.TokenUses.Service)
            ], "Bearer"));
        httpContext.Request.Headers.Authorization = "Bearer svc-token";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("mine", null);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Inventory.Forbidden, result.Error.Code);
    }

    [Fact]
    public async Task InventoryServiceClient_MineScope_Unauthenticated_Returns401()
    {
        var httpClient = new HttpClient(new DelegatingMockHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            BaseAddress = new Uri("http://localhost:5003/")
        };
        var httpContext = new DefaultHttpContext(); // No user
        var accessor = new HttpContextAccessor { HttpContext = httpContext };

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("mine", null);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Inventory.Unauthorized, result.Error.Code);
    }

    [Fact]
    public async Task InventoryServiceClient_ConcurrentRequests_DoNotLeakTokens()
    {
        var capturedTokens = new System.Collections.Concurrent.ConcurrentBag<string?>();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var handler = new DelegatingMockHandler(async (req, ct) =>
        {
            string? token = req.Headers.Authorization?.Parameter;
            if (Interlocked.Increment(ref started) == 10)
                allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(token, req.Headers.Authorization?.Parameter);
            capturedTokens.Add(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var accessor = new HttpContextAccessor();

        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var ctx = new DefaultHttpContext();
            var token = $"token-user-{i}";
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(CatalogClaims.Types.Subject, Guid.NewGuid().ToString())], "Bearer"));
            ctx.Request.Headers.Authorization = $"Bearer {token}";
            SaveAuthenticatedToken(ctx, token);

            // Set context for this async flow
            accessor.HttpContext = ctx;

            var res = await client.GetListingStatisticsAsync("mine", null);
            Assert.True(res.IsSuccess);
        });

        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"token-user-{i}").Order(), capturedTokens.Order());
    }

    [Theory]
    [InlineData("{\"byEquipmentType\":null,\"byBrand\":{}}")] // null dictionary
    [InlineData("{\"byBrand\":{}}")] // missing byEquipmentType
    [InlineData("{\"byEquipmentType\":{},\"byBrand\":null}")] // null byBrand
    [InlineData("{\"byEquipmentType\":{\"not-a-guid\":5},\"byBrand\":{}}")] // invalid guid key
    [InlineData("{\"byEquipmentType\":{},\"byBrand\":{\"00000000-0000-0000-0000-000000000001\":-1}}")] // negative count
    [InlineData("not json")] // malformed json
    public async Task InventoryServiceClient_StrictContractValidation_Returns503(string responseJson)
    {
        var handler = new DelegatingMockHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            }));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5003/") };
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var client = new InventoryServiceClient(httpClient, accessor, NullLogger<InventoryServiceClient>.Instance);

        var result = await client.GetListingStatisticsAsync("public", null);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Inventory.ServiceUnavailable, result.Error.Code);
    }

    #endregion

    private static void SaveAuthenticatedToken(DefaultHttpContext context, string token)
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = token }]);
        context.Features.Set<IAuthenticateResultFeature>(new SavedAuthenticationFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, properties, "Bearer"))
        });
    }
    private sealed class SavedAuthenticationFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(503)]
    public async Task StatusMapping_Requires200(int status)
    {
        using var http = new HttpClient(new DelegatingMockHandler((_, _) => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("{\"byEquipmentType\":{},\"byBrand\":{}}") })))
        { BaseAddress = new Uri("http://inventory.test/") };
        var client = new InventoryServiceClient(http, new HttpContextAccessor(), NullLogger<InventoryServiceClient>.Instance);
        var result = await client.GetListingStatisticsAsync("public", null);
        Assert.True(result.IsFailure);
        Assert.Equal(status switch { 401 => ErrorType.Unauthorized, 403 => ErrorType.Forbidden, _ => ErrorType.Unavailable }, result.Error.Type);
    }

    [Fact]
    public async Task MineScope_DoesNotTrustRawHeaderWithoutSavedAuthentication()
    {
        using var http = new HttpClient(new DelegatingMockHandler((_, _) => throw new InvalidOperationException("Must not send")))
        { BaseAddress = new Uri("http://inventory.test/") };
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "Bearer")) };
        context.Request.Headers.Authorization = "Bearer raw-token";
        var client = new InventoryServiceClient(http, new HttpContextAccessor { HttpContext = context }, NullLogger<InventoryServiceClient>.Instance);
        var result = await client.GetListingStatisticsAsync("mine", null);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Inventory.Unauthorized, result.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailure_Returns503(bool timeout)
    {
        using var http = new HttpClient(new DelegatingMockHandler((_, _) =>
            throw (timeout ? new OperationCanceledException() : new HttpRequestException("Unavailable"))))
        { BaseAddress = new Uri("http://inventory.test/") };
        var client = new InventoryServiceClient(http, new HttpContextAccessor(), NullLogger<InventoryServiceClient>.Instance);
        var result = await client.GetListingStatisticsAsync("public", null);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Inventory.ServiceUnavailable, result.Error.Code);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new DelegatingMockHandler(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        { BaseAddress = new Uri("http://inventory.test/") };
        var client = new InventoryServiceClient(http, new HttpContextAccessor(), NullLogger<InventoryServiceClient>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetListingStatisticsAsync("public", null, cancellation.Token));
    }
}
