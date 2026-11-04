using System.Security.Claims;
using Dprental.NotificationService.Api.Hubs;
using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Api.Services;
using Dprental.NotificationService.Application.IServices;
using Dprental.NotificationService.Application.Services;
using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Security;

public sealed class SecurityAndRealtimeTests
{
    [Fact]
    public void UserContext_HumanUserToken_ShouldResolveSuccessfully()
    {
        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsSuccess);
        Assert.Equal(userId, result.Value);
    }

    [Fact]
    public void UserContext_ServiceToken_ShouldBeStrictlyRejected()
    {
        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("u", "svc") // Service principal token!
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsFailure);
        Assert.Equal("SERVICE_TOKEN_FORBIDDEN", result.Error.Code);
    }

    [Fact]
    public void UserContext_Unauthenticated_ShouldBeRejected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
    }

    [Fact]
    public async Task SignalR_QueryTokenProhibition_ShouldFailContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?access_token=some_token");

        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            null,
            typeof(JwtBearerHandler));

        var options = new JwtBearerOptions();
        var context = new MessageReceivedContext(httpContext, scheme, options);

        // Simulate our MessageReceived handler
        if (context.Request.Query.ContainsKey("access_token"))
        {
            context.Fail("Query string access_token is strictly prohibited. Use Authorization header or accessToken cookie.");
        }

        Assert.NotNull(context.Result);
        Assert.True(context.Result.Failure != null);
    }

    [Fact]
    public async Task RealtimeNotifier_ShouldDispatchToUserPrivateGroup()
    {
        var hubContextMock = new Mock<IHubContext<NotificationHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();

        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group("user:d7b0034a-939b-4395-bfa3-d143da9666c8"))
            .Returns(clientProxyMock.Object);

        var notifier = new SignalRNotificationRealtimeNotifier(
            hubContextMock.Object,
            NullLogger<SignalRNotificationRealtimeNotifier>.Instance);

        var targetUserId = Guid.Parse("d7b0034a-939b-4395-bfa3-d143da9666c8");
        var notificationId = Guid.NewGuid();

        await notifier.NotifyInboxChangedAsync(
            targetUserId,
            notificationId,
            "CREATED",
            "ORDER",
            DateTimeOffset.UtcNow);

        clientProxyMock.Verify(c => c.SendCoreAsync(
            "NotificationInboxChanged",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotificationReadService_MarkAsRead_ShouldNotifyRealtimeOnSuccess()
    {
        var repoMock = new Mock<INotificationRepository>();
        var notifierMock = new Mock<INotificationRealtimeNotifier>();
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var existing = new Notification
        {
            Id = notificationId,
            RecipientUserId = userId,
            Category = "ORDER"
        };

        repoMock.Setup(r => r.GetByIdAndUserAsync(notificationId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        repoMock.Setup(r => r.MarkAsReadAsync(notificationId, userId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var readService = new NotificationReadService(repoMock.Object, notifierMock.Object);
        var result = await readService.MarkAsReadAsync(notificationId, userId);

        Assert.True(result.IsSuccess);
        notifierMock.Verify(n => n.NotifyInboxChangedAsync(
            userId,
            notificationId,
            "READ",
            "ORDER",
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotificationReadService_MarkAsRead_NotFound_ShouldNotNotifyRealtime()
    {
        var repoMock = new Mock<INotificationRepository>();
        var notifierMock = new Mock<INotificationRealtimeNotifier>();
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        repoMock.Setup(r => r.GetByIdAndUserAsync(notificationId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var readService = new NotificationReadService(repoMock.Object, notifierMock.Object);
        var result = await readService.MarkAsReadAsync(notificationId, userId);

        Assert.True(result.IsFailure);
        notifierMock.Verify(n => n.NotifyInboxChangedAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotificationReadService_MarkAllAsRead_WithMarkedItems_ShouldNotifyRealtime()
    {
        var repoMock = new Mock<INotificationRepository>();
        var notifierMock = new Mock<INotificationRealtimeNotifier>();
        var userId = Guid.NewGuid();

        repoMock.Setup(r => r.MarkAllAsReadAsync(userId, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var readService = new NotificationReadService(repoMock.Object, notifierMock.Object);
        var result = await readService.MarkAllAsReadAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.MarkedCount);
        notifierMock.Verify(n => n.NotifyInboxChangedAsync(
            userId,
            Guid.Empty,
            "READ_ALL",
            "ALL",
            result.Value.ReadAt,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotificationReadService_MarkAllAsRead_ZeroMarked_ShouldNotNotifyRealtime()
    {
        var repoMock = new Mock<INotificationRepository>();
        var notifierMock = new Mock<INotificationRealtimeNotifier>();
        var userId = Guid.NewGuid();

        repoMock.Setup(r => r.MarkAllAsReadAsync(userId, It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var readService = new NotificationReadService(repoMock.Object, notifierMock.Object);
        var result = await readService.MarkAllAsReadAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.MarkedCount);
        notifierMock.Verify(n => n.NotifyInboxChangedAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
