using Dprental.Gateway.Middlewares.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Dprental.Gateway.Tests;

public class InternalAuthRouteTests
{
    [Fact]
    public async Task InternalAuthEndpointsCannotBeReachedThroughGateway()
    {
        var called = false;
        var middleware = new ServiceOnlyRouteBlockingMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/auth/internal/supplier-organizations/by-owner/00000000-0000-0000-0000-000000000001";
        await middleware.InvokeAsync(context);
        Assert.False(called);
        Assert.Equal(404, context.Response.StatusCode);
    }
}
