using System.Reflection;
using System.Security.Claims;
using Dprental.RentalService.Api.Hubs;
using Dprental.RentalService.Api.Hubs.Security;
using Dprental.RentalService.Application.Options;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Hubs;

public sealed class OrderHubUnitTests
{
    [Fact]
    public async Task OnConnectedAsync_ValidInteractiveUser_AutoJoinsRenterAndSupplierGroups_WithNormalizedGuid()
    {
        Guid userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("aud", "dprental-clients")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var mockGroups = new Mock<IGroupManager>();
        var mockContext = new Mock<HubCallerContext>();
        string connectionId = "conn-12345";
        bool aborted = false;

        mockContext.SetupGet(c => c.ConnectionId).Returns(connectionId);
        mockContext.SetupGet(c => c.User).Returns(principal);
        mockContext.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = new OrderHub(NullLogger<OrderHub>.Instance)
        {
            Context = mockContext.Object,
            Groups = mockGroups.Object
        };

        await hub.OnConnectedAsync();

        Assert.False(aborted, "Connection should not be aborted for a valid human user.");
        string expectedRenterGroup = $"renter:{userId:D}";
        string expectedSupplierGroup = $"supplier:{userId:D}";

        mockGroups.Verify(g => g.AddToGroupAsync(connectionId, expectedRenterGroup, default), Times.Once);
        mockGroups.Verify(g => g.AddToGroupAsync(connectionId, expectedSupplierGroup, default), Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_ServiceToken_RejectsAndAbortsConnection()
    {
        Guid serviceId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", serviceId.ToString()),
            new Claim("u", "svc"),
            new Claim("aud", "dprental-clients")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var mockGroups = new Mock<IGroupManager>();
        var mockContext = new Mock<HubCallerContext>();
        bool aborted = false;

        mockContext.SetupGet(c => c.ConnectionId).Returns("conn-service");
        mockContext.SetupGet(c => c.User).Returns(principal);
        mockContext.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = new OrderHub(NullLogger<OrderHub>.Instance)
        {
            Context = mockContext.Object,
            Groups = mockGroups.Object
        };

        await hub.OnConnectedAsync();

        Assert.True(aborted, "Connection should be aborted when caller is a service token.");
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_AnonymousOrEmptySub_RejectsAndAbortsConnection()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // Unauthenticated

        var mockGroups = new Mock<IGroupManager>();
        var mockContext = new Mock<HubCallerContext>();
        bool aborted = false;

        mockContext.SetupGet(c => c.ConnectionId).Returns("conn-anon");
        mockContext.SetupGet(c => c.User).Returns(principal);
        mockContext.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = new OrderHub(NullLogger<OrderHub>.Instance)
        {
            Context = mockContext.Object,
            Groups = mockGroups.Object
        };

        await hub.OnConnectedAsync();

        Assert.True(aborted, "Connection should be aborted when caller is anonymous.");
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_GroupJoinException_AbortsConnection()
    {
        Guid userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("aud", "dprental-clients")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var mockGroups = new Mock<IGroupManager>();
        mockGroups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ThrowsAsync(new InvalidOperationException("SignalR group error."));

        var mockContext = new Mock<HubCallerContext>();
        bool aborted = false;

        mockContext.SetupGet(c => c.ConnectionId).Returns("conn-fail");
        mockContext.SetupGet(c => c.User).Returns(principal);
        mockContext.Setup(c => c.Abort()).Callback(() => aborted = true);

        var hub = new OrderHub(NullLogger<OrderHub>.Instance)
        {
            Context = mockContext.Object,
            Groups = mockGroups.Object
        };

        await hub.OnConnectedAsync();

        Assert.True(aborted, "Connection should be aborted if group join throws.");
    }

    [Fact]
    public void OrderHub_HasNoPublicClientCallableMethods_ToManipulateMembershipOrSend()
    {
        // Client cannot control membership via userId/orderId/supplierId or send messages
        MethodInfo[] declaredPublicMethods = typeof(OrderHub).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // Only lifecycle overrides like OnConnectedAsync are allowed if any; no custom public methods
        string[] customMethods = declaredPublicMethods
            .Where(m => m.Name != nameof(OrderHub.OnConnectedAsync) && m.Name != nameof(OrderHub.OnDisconnectedAsync))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(customMethods);
    }

    [Fact]
    public async Task OriginGuardMiddleware_BrowserUnallowedOrigin_Returns403()
    {
        var options = Options.Create(new OrderHubOptions
        {
            AllowedOrigins = ["http://localhost:3000", "https://dprental.com"]
        });

        var middleware = new OrderHubOriginGuardMiddleware(
            _ => Task.CompletedTask,
            options,
            NullLogger<OrderHubOriginGuardMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/rental/hubs/orders/negotiate";
        context.Request.Headers["Origin"] = "http://attacker-site.com";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task OriginGuardMiddleware_BrowserAllowedOrigin_CallsNext()
    {
        var options = Options.Create(new OrderHubOptions
        {
            AllowedOrigins = ["http://localhost:3000", "https://dprental.com"]
        });

        bool nextCalled = false;
        var middleware = new OrderHubOriginGuardMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options,
            NullLogger<OrderHubOriginGuardMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/rental/hubs/orders";
        context.Request.Headers["Origin"] = "http://localhost:3000";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task OriginGuardMiddleware_NonBrowserMissingOrigin_CallsNext()
    {
        var options = Options.Create(new OrderHubOptions
        {
            AllowedOrigins = ["http://localhost:3000", "https://dprental.com"]
        });

        bool nextCalled = false;
        var middleware = new OrderHubOriginGuardMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options,
            NullLogger<OrderHubOriginGuardMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/rental/hubs/orders/negotiate";
        // No Origin header

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
}
