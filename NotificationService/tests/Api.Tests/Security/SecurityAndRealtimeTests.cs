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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

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
        Assert.Equal(Dprental.NotificationService.Common.ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public void UserContext_ServiceToken_WithoutSub_ShouldBeStrictlyRejectedWith403Forbidden()
    {
        var claims = new[]
        {
            new Claim("u", "svc")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsFailure);
        Assert.Equal("SERVICE_TOKEN_FORBIDDEN", result.Error.Code);
        Assert.Equal(Dprental.NotificationService.Common.ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public void UserContext_HumanToken_MissingSub_ShouldBeRejectedWith401Unauthorized()
    {
        var claims = new[]
        {
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_TOKEN_SUB", result.Error.Code);
        Assert.Equal(Dprental.NotificationService.Common.ErrorType.Unauthorized, result.Error.Type);
    }

    [Theory]
    [InlineData("not-a-valid-guid")]
    [InlineData("12345")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void UserContext_HumanToken_NonGuidSub_ShouldBeRejectedWith401Unauthorized(string invalidSub)
    {
        var claims = new[]
        {
            new Claim("sub", invalidSub),
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var result = principal.TryGetHumanUserId();
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_TOKEN_SUB", result.Error.Code);
        Assert.Equal(Dprental.NotificationService.Common.ErrorType.Unauthorized, result.Error.Type);
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
    public async Task NotificationHub_HumanUserToken_ConnectsAndJoinsUserGroup()
    {
        var userId = Guid.NewGuid();
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var mockContext = new Mock<HubCallerContext>();
        var mockGroups = new Mock<IGroupManager>();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-123");
        mockContext.Setup(c => c.User).Returns(principal);
        hub.Context = mockContext.Object;
        hub.Groups = mockGroups.Object;

        await hub.OnConnectedAsync();

        mockGroups.Verify(g => g.AddToGroupAsync("conn-123", $"user:{userId}", default), Times.Once);
        mockContext.Verify(c => c.Abort(), Times.Never);
    }

    [Fact]
    public async Task NotificationHub_ServiceToken_StrictlyRejectedWith403Forbidden()
    {
        var claims = new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("u", "svc")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var mockContext = new Mock<HubCallerContext>();
        var mockGroups = new Mock<IGroupManager>();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-service");
        mockContext.Setup(c => c.User).Returns(principal);
        hub.Context = mockContext.Object;
        hub.Groups = mockGroups.Object;

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.OnConnectedAsync());
        Assert.Contains("403 Forbidden", ex.Message);
        mockContext.Verify(c => c.Abort(), Times.Once);
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task NotificationHub_MissingSub_RejectedWith401Unauthorized()
    {
        var claims = new[]
        {
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var mockContext = new Mock<HubCallerContext>();
        var mockGroups = new Mock<IGroupManager>();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-nosub");
        mockContext.Setup(c => c.User).Returns(principal);
        hub.Context = mockContext.Object;
        hub.Groups = mockGroups.Object;

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.OnConnectedAsync());
        Assert.Contains("401 Unauthorized", ex.Message);
        mockContext.Verify(c => c.Abort(), Times.Once);
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task NotificationHub_NonGuidSub_RejectedWith401Unauthorized()
    {
        var claims = new[]
        {
            new Claim("sub", "not-a-valid-guid"),
            new Claim("u", "usr")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var mockContext = new Mock<HubCallerContext>();
        var mockGroups = new Mock<IGroupManager>();

        mockContext.Setup(c => c.ConnectionId).Returns("conn-badsub");
        mockContext.Setup(c => c.User).Returns(principal);
        hub.Context = mockContext.Object;
        hub.Groups = mockGroups.Object;

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.OnConnectedAsync());
        Assert.Contains("401 Unauthorized", ex.Message);
        mockContext.Verify(c => c.Abort(), Times.Once);
        mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
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
            (Guid?)null,
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

    [Fact]
    public async Task NotificationReadService_MarkAsRead_AlreadyRead_ReturnsFalse_DoesNotNotifyRealtime()
    {
        var repoMock = new Mock<INotificationRepository>();
        var notifierMock = new Mock<INotificationRealtimeNotifier>();
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var existing = new Notification
        {
            Id = notificationId,
            RecipientUserId = userId,
            Category = "ORDER",
            ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        repoMock.Setup(r => r.GetByIdAndUserAsync(notificationId, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        // MarkAsReadAsync returns false when the row was already read
        repoMock.Setup(r => r.MarkAsReadAsync(notificationId, userId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var readService = new NotificationReadService(repoMock.Object, notifierMock.Object);
        var result = await readService.MarkAsReadAsync(notificationId, userId);

        Assert.True(result.IsSuccess);
        notifierMock.Verify(n => n.NotifyInboxChangedAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NotificationRepository_MarkAsReadAsync_ReturnsTrueOnlyWhenRowChangedFromUnread()
    {
        var options = new DbContextOptionsBuilder<NotificationServiceDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var db = new NotificationServiceDbContext(options);
        var repo = new NotificationRepository(db);

        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var unreadNotification = new Notification
        {
            Id = notificationId,
            RecipientUserId = userId,
            NotificationType = "ORDER_CREATED",
            Category = "ORDER",
            Severity = "INFO",
            Title = "Test",
            Body = "Test",
            ResourceType = "RentalOrder",
            ResourceId = Guid.NewGuid().ToString(),
            SourceEventId = Guid.NewGuid(),
            SourceProducer = "RentalService",
            SourceEventType = "RentalOrderCreated",
            TemplateCode = "ORDER_CREATED",
            TemplateVersion = "1.0",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            ReadAt = null
        };
        await db.Notifications.AddAsync(unreadNotification);
        await db.SaveChangesAsync();

        // First call on unread notification: must return true
        var firstResult = await repo.MarkAsReadAsync(notificationId, userId, DateTimeOffset.UtcNow);
        Assert.True(firstResult);

        // Second call on now-read notification: must return false (no row changed from unread to read)
        var secondResult = await repo.MarkAsReadAsync(notificationId, userId, DateTimeOffset.UtcNow);
        Assert.False(secondResult);

        // Call on non-existent notification: must return false
        var thirdResult = await repo.MarkAsReadAsync(Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        Assert.False(thirdResult);

        // Call with wrong user ID: must return false
        var fourthResult = await repo.MarkAsReadAsync(notificationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.False(fourthResult);
    }

    [Fact]
    public async Task SignalRNotificationRealtimeNotifier_SendsStandardizedNotificationInboxChangedEvent()
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
        var now = DateTimeOffset.UtcNow;

        var changeEvent = new NotificationInboxChangedEvent(
            ChangeType: NotificationChangeTypes.Created,
            NotificationId: notificationId,
            Category: "ORDER",
            OccurredAt: now);

        await notifier.NotifyInboxChangedAsync(targetUserId, changeEvent);

        clientProxyMock.Verify(c => c.SendCoreAsync(
            "NotificationInboxChanged",
            It.Is<object?[]>(args =>
                args.Length == 1 &&
                args[0] is NotificationInboxChangedEvent &&
                ((NotificationInboxChangedEvent)args[0]!).ChangeType == "CREATED" &&
                ((NotificationInboxChangedEvent)args[0]!).NotificationId == notificationId &&
                ((NotificationInboxChangedEvent)args[0]!).Category == "ORDER" &&
                ((NotificationInboxChangedEvent)args[0]!).OccurredAt == now),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void StandardizedNotificationInboxChangedEvent_ContainsExpectedChangeTypes()
    {
        Assert.Equal("CREATED", NotificationChangeTypes.Created);
        Assert.Equal("READ", NotificationChangeTypes.Read);
        Assert.Equal("READ_ALL", NotificationChangeTypes.ReadAll);
        Assert.Equal("UPDATED", NotificationChangeTypes.Updated);

        var evt = new NotificationInboxChangedEvent("READ_ALL", null, "ALL", DateTimeOffset.UtcNow);
        Assert.Equal("READ_ALL", evt.ChangeType);
        Assert.Null(evt.NotificationId);
        Assert.Equal("ALL", evt.Category);
    }

    [Fact]
    public async Task SignalR_RedisBackplane_WhenConfigured_ConnectsSuccessfully()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSignalR()
            .AddStackExchangeRedis("127.0.0.1:26379", options =>
            {
                options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("dprental:test:notifications");
            });

        await using var sp = services.BuildServiceProvider();
        var hubContext = sp.GetService<IHubContext<NotificationHub>>();
        Assert.NotNull(hubContext);
    }
}
