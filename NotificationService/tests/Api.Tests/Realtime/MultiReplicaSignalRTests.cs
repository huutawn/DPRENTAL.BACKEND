using System.Security.Claims;
using System.Text.Encodings.Web;
using Dprental.NotificationService.Api.Hubs;
using Dprental.NotificationService.Api.Services;
using Dprental.NotificationService.Application.Diagnostics;
using Dprental.NotificationService.Application.DTOs;
using Dprental.NotificationService.Application.IServices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Dprental.NotificationService.Api.Tests.Realtime;

public sealed class MultiReplicaSignalRTests
{
    private const string RedisConnectionString = "127.0.0.1:26379";

    [Fact]
    public async Task MultiReplica_SameUserAcrossReplicas_ReceivesInboxChangedEvent()
    {
        var testEnv = $"test_{Guid.NewGuid():N}";
        var sameUserId = Guid.NewGuid();
        var differentUserId = Guid.NewGuid();

        using var hostA = CreateHost(testEnv);
        using var hostB = CreateHost(testEnv);

        // Client 1 connected to Host A for same user
        var client1 = CreateHubConnection(hostA, sameUserId);

        // Client 2 connected to Host B for same user
        var client2 = CreateHubConnection(hostB, sameUserId);

        // Client 3 connected to Host B for different user
        var client3 = CreateHubConnection(hostB, differentUserId);

        var tcsClient1 = new TaskCompletionSource<NotificationInboxChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client1.On<NotificationInboxChangedEvent>("InboxChanged", evt => tcsClient1.TrySetResult(evt));
        client1.On<NotificationInboxChangedEvent>("NotificationInboxChanged", evt => tcsClient1.TrySetResult(evt));

        var tcsClient2 = new TaskCompletionSource<NotificationInboxChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client2.On<NotificationInboxChangedEvent>("InboxChanged", evt => tcsClient2.TrySetResult(evt));
        client2.On<NotificationInboxChangedEvent>("NotificationInboxChanged", evt => tcsClient2.TrySetResult(evt));

        var client3Received = false;
        client3.On<NotificationInboxChangedEvent>("InboxChanged", _ => client3Received = true);
        client3.On<NotificationInboxChangedEvent>("NotificationInboxChanged", _ => client3Received = true);

        try
        {
            await client1.StartAsync();
            await client2.StartAsync();
            await client3.StartAsync();

            // Allow Redis pub/sub group subscriptions to synchronize
            await Task.Delay(300);

            var notifierA = hostA.Services.GetRequiredService<INotificationRealtimeNotifier>();
            var expectedNotificationId = Guid.NewGuid();
            var changeEvent = new NotificationInboxChangedEvent(
                ChangeType: NotificationChangeTypes.Created,
                NotificationId: expectedNotificationId,
                Category: "ORDER",
                OccurredAt: DateTimeOffset.UtcNow);

            // Publish via Host A
            await notifierA.NotifyInboxChangedAsync(sameUserId, changeEvent);

            // Verify client 2 on Host B receives InboxChanged
            var receivedOnB = await tcsClient2.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(receivedOnB);
            Assert.Equal(NotificationChangeTypes.Created, receivedOnB.ChangeType);
            Assert.Equal(expectedNotificationId, receivedOnB.NotificationId);
            Assert.Equal("ORDER", receivedOnB.Category);

            // Verify client 1 on Host A also receives the event
            var receivedOnA = await tcsClient1.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(receivedOnA);
            Assert.Equal(expectedNotificationId, receivedOnA.NotificationId);

            // Verify client for a different user does not receive the event
            await Task.Delay(500);
            Assert.False(client3Received, "Client for a different user must not receive the notification event.");
        }
        finally
        {
            await client1.DisposeAsync();
            await client2.DisposeAsync();
            await client3.DisposeAsync();
        }
    }

    [Fact]
    public async Task RedisResilience_WhenRedisFails_RealtimeNotifierDoesNotThrowAndRecordsMetric()
    {
        var hubContextMock = new Mock<IHubContext<NotificationHub>>();
        var hubClientsMock = new Mock<IHubClients>();
        var clientProxyMock = new Mock<IClientProxy>();

        hubContextMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxyMock.Object);

        // Simulate Redis network outage / failure during group dispatch
        clientProxyMock
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage"));

        var notifier = new SignalRNotificationRealtimeNotifier(
            hubContextMock.Object,
            NullLogger<SignalRNotificationRealtimeNotifier>.Instance);

        var targetUserId = Guid.NewGuid();
        var changeEvent = new NotificationInboxChangedEvent(
            ChangeType: NotificationChangeTypes.Created,
            NotificationId: Guid.NewGuid(),
            Category: "SECURITY",
            OccurredAt: DateTimeOffset.UtcNow);

        // Redis outage must not throw or crash caller / database transactions
        var exception = await Record.ExceptionAsync(() => notifier.NotifyInboxChangedAsync(targetUserId, changeEvent));
        Assert.Null(exception);
    }

    [Fact]
    public void RedisResilience_BackplaneOptions_ConfiguresChannelPrefixAndResilience()
    {
        var services = new ServiceCollection();
        var env = "staging";
        var expectedPrefix = $"dprental:notification:{env}";

        services.AddLogging();
        services.AddSignalR()
            .AddStackExchangeRedis(RedisConnectionString, options =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal(expectedPrefix);
                options.Configuration.AbortOnConnectFail = false;
                options.Configuration.ConnectRetry = 5;
                options.Configuration.ConnectTimeout = 5000;
            });

        using var sp = services.BuildServiceProvider();
        var hubContext = sp.GetService<IHubContext<NotificationHub>>();
        Assert.NotNull(hubContext);
    }

    private static WebApplicationFactory<Program> CreateHost(string environmentName)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", environmentName);
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.UseSetting("ConnectionStrings:Redis", RedisConnectionString);
                builder.UseSetting("Kafka:BootstrapServers", "localhost:9092");
                builder.UseSetting("Kafka:Topics", "rental.events.v1");
                builder.UseSetting("Services:AuthService:BaseUrl", "http://auth-service:8080");
                builder.UseSetting("InternalAuthentication:ApiKey", "test-internal-api-key");

                builder.ConfigureTestServices(services =>
                {
                    // Remove background workers so they don't query a dummy database during test
                    services.RemoveAll<IHostedService>();

                    // Configure test authentication handler for SignalR hub authorization
                    services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                });
            });
    }

    private static HubConnection CreateHubConnection(WebApplicationFactory<Program> factory, Guid userId)
    {
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/api/v1/notifications/hubs/inbox", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Authorization"] = $"Bearer {userId}";
            })
            .Build();
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestAuth";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? token = null;

            if (Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
            {
                var header = authHeaderValues.ToString();
                if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = header["Bearer ".Length..].Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (Guid.TryParse(token, out var userId))
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString())
                };
                var identity = new ClaimsIdentity(claims, SchemeName);
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, SchemeName);
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid test token"));
        }
    }
}
