using System.Security.Claims;
using Dprental.PaymentService.Application.Hub;
using Dprental.PaymentService.Application.Hub.Contracts;
using Dprental.PaymentService.Application.Hub.Groups;
using Dprental.PaymentService.Application.Hub.Options;
using Dprental.PaymentService.Application.Hub.Security;
using Dprental.PaymentService.Application.Security.Jwt;
using Dprental.PaymentService.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Unit;

public sealed class PaymentHubTests
{
    [Fact]
    public void UserIdProvider_ReturnsNormalizedGuid_ForValidClientToken()
    {
        var userId = Guid.NewGuid();
        var jwtOptions = Options.Create(new PaymentJwtOptions
        {
            ClientAudience = "dprental-clients",
            ServiceAudience = "payment-api"
        });

        var provider = new PaymentUserIdProvider(jwtOptions);

        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("aud", "dprental-clients")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        var context = CreateHubConnectionContext(principal);
        var result = provider.GetUserId(context);
        Assert.Equal(userId.ToString("D"), result);
    }

    [Fact]
    public void UserIdProvider_RejectsServiceToken()
    {
        var jwtOptions = Options.Create(new PaymentJwtOptions
        {
            ClientAudience = "dprental-clients",
            ServiceAudience = "payment-api"
        });
        var provider = new PaymentUserIdProvider(jwtOptions);

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("aud", "dprental-clients"),
            new Claim("u", "svc")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        var context = CreateHubConnectionContext(principal);
        var result = provider.GetUserId(context);
        Assert.Null(result);
    }

    [Fact]
    public void UserIdProvider_RejectsMismatchedAudience()
    {
        var jwtOptions = Options.Create(new PaymentJwtOptions
        {
            ClientAudience = "dprental-clients",
            ServiceAudience = "payment-api"
        });
        var provider = new PaymentUserIdProvider(jwtOptions);

        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("aud", "other-api")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        var context = CreateHubConnectionContext(principal);
        var result = provider.GetUserId(context);
        Assert.Null(result);
    }

    [Fact]
    public void PaymentHubGroups_TracksConnectionsAndEnforcesLimits()
    {
        var options = Options.Create(new PaymentHubOptions
        {
            MaxConnections = 2,
            MaxGroups = 2,
            MaxUsersPerGroup = 2
        });
        var groups = new PaymentHubGroups(options);
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();

        var res1 = groups.TryRegisterConnection("conn-1", user1, out _);
        Assert.True(res1.IsSuccess);
        var res2 = groups.TryRegisterConnection("conn-2", user2, out _);
        Assert.True(res2.IsSuccess);

        // Exceeds MaxConnections
        var res3 = groups.TryRegisterConnection("conn-3", user3, out _);
        Assert.True(res3.IsFailure);
        Assert.Equal("PaymentHub.MaxConnectionsExceeded", res3.Error.Code);

        // Disconnect one, then reconnect
        groups.UnregisterConnection("conn-1", out var unregUser);
        Assert.Equal(user1, unregUser);
        Assert.Equal(1, groups.ActiveConnectionsCount);

        var res4 = groups.TryRegisterConnection("conn-4", user3, out _);
        Assert.True(res4.IsSuccess);
        Assert.Equal(2, groups.ActiveConnectionsCount);
    }

    [Fact]
    public void PaymentHubGroups_SetGroupUsers_EnforcesLimitsAndManagesMembership()
    {
        var options = Options.Create(new PaymentHubOptions
        {
            MaxConnections = 100,
            MaxGroups = 1,
            MaxUsersPerGroup = 2
        });
        var groups = new PaymentHubGroups(options);

        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var u3 = Guid.NewGuid();

        // Too many users for a group
        var overLimit = groups.SetGroupUsers("group-1", [u1, u2, u3], out _, out _);
        Assert.True(overLimit.IsFailure);
        Assert.Equal("PaymentHub.MaxGroupUsersExceeded", overLimit.Error.Code);

        // Valid group
        var valid = groups.SetGroupUsers("group-1", [u1, u2], out var toAdd, out var toRemove);
        Assert.True(valid.IsSuccess);
        Assert.Equal(1, groups.GroupsCount);

        // Exceeds max groups
        var secondGroup = groups.SetGroupUsers("group-2", [u1], out _, out _);
        Assert.True(secondGroup.IsFailure);
        Assert.Equal("PaymentHub.MaxGroupsExceeded", secondGroup.Error.Code);

        // Remove group
        var remove = groups.RemoveGroup("group-1", out _);
        Assert.True(remove.IsSuccess);
        Assert.Equal(0, groups.GroupsCount);
    }

    [Fact]
    public async Task OriginGuard_RejectsUnallowedOrigin()
    {
        var options = Options.Create(new PaymentHubOptions
        {
            HubPath = "/hubs/payments",
            AllowedOrigins = ["http://localhost:3000"]
        });

        var middleware = new PaymentHubOriginGuardMiddleware(
            _ => Task.CompletedTask, options, NullLogger<PaymentHubOriginGuardMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/payments";
        context.Request.Headers["Origin"] = "http://evil.com";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task OriginGuard_AllowsConfiguredOrigin()
    {
        var options = Options.Create(new PaymentHubOptions
        {
            HubPath = "/hubs/payments",
            AllowedOrigins = ["http://localhost:3000"]
        });

        bool nextCalled = false;
        var middleware = new PaymentHubOriginGuardMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            options, NullLogger<PaymentHubOriginGuardMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/payments/negotiate";
        context.Request.Headers["Origin"] = "http://localhost:3000";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }
    private static HubConnectionContext CreateHubConnectionContext(ClaimsPrincipal user)
    {
        var connectionContext = new Microsoft.AspNetCore.Connections.DefaultConnectionContext
        {
            User = user
        };
        return new HubConnectionContext(connectionContext, new HubConnectionContextOptions(), NullLoggerFactory.Instance);
    }
}
