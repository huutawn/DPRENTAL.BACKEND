using Dprental.AnalyticsService.Application.Hub;
using Dprental.AnalyticsService.Application.Hub.Contracts;
using Dprental.AnalyticsService.Application.Hub.Options;
using Dprental.AnalyticsService.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Hub;

public sealed class AnalyticsHubNotifierTests
{
    private static readonly Guid SupplierId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AdminFails_SupplierStillReceives_ReturnsFailure()
    {
        var testClients = new TestHubClients
        {
            GroupHandler = (group, method, args) => throw new InvalidOperationException("Admin send failed"),
            UserHandler = (users, method, args) => Task.CompletedTask
        };
        var hubContext = new TestHubContext(testClients);
        var options = Options.Create(new AnalyticsHubOptions { SendTimeoutSeconds = 1 });
        var notifier = new AnalyticsHubNotifier(hubContext, options, TimeProvider.System, NullLogger<AnalyticsHubNotifier>.Instance);

        Result result = await notifier.NotifyDataChangedAsync(AnalyticsDataScopes.Finance, [SupplierId], CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AnalyticsHub.DeliveryFailed", result.Error.Code);
        Assert.Single(testClients.UserInvocations);
        Assert.Equal(SupplierId.ToString("D"), testClients.UserInvocations[0].Users[0]);
    }

    [Fact]
    public async Task SupplierFails_AdminAlreadyReceived_ReturnsFailure()
    {
        var testClients = new TestHubClients
        {
            GroupHandler = (group, method, args) => Task.CompletedTask,
            UserHandler = (users, method, args) => throw new InvalidOperationException("Supplier send failed")
        };
        var hubContext = new TestHubContext(testClients);
        var options = Options.Create(new AnalyticsHubOptions { SendTimeoutSeconds = 1 });
        var notifier = new AnalyticsHubNotifier(hubContext, options, TimeProvider.System, NullLogger<AnalyticsHubNotifier>.Instance);

        Result result = await notifier.NotifyDataChangedAsync(AnalyticsDataScopes.Finance, [SupplierId], CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AnalyticsHub.DeliveryFailed", result.Error.Code);
        Assert.Single(testClients.GroupInvocations);
    }

    [Fact]
    public async Task BothSucceed_ReturnsSuccess_WithMatchingEnvelope()
    {
        var testClients = new TestHubClients
        {
            GroupHandler = (group, method, args) => Task.CompletedTask,
            UserHandler = (users, method, args) => Task.CompletedTask
        };
        var hubContext = new TestHubContext(testClients);
        var options = Options.Create(new AnalyticsHubOptions { SendTimeoutSeconds = 1 });
        var notifier = new AnalyticsHubNotifier(hubContext, options, TimeProvider.System, NullLogger<AnalyticsHubNotifier>.Instance);

        Result result = await notifier.NotifyDataChangedAsync(AnalyticsDataScopes.Finance, [SupplierId], CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(testClients.GroupInvocations);
        Assert.Single(testClients.UserInvocations);

        var adminEnv = (AnalyticsHubEnvelope<AnalyticsDataChangedData>)testClients.GroupInvocations[0].Args[0]!;
        var supplierEnv = (AnalyticsHubEnvelope<AnalyticsDataChangedData>)testClients.UserInvocations[0].Args[0]!;
        Assert.Equal(adminEnv.EventId, supplierEnv.EventId);
        Assert.Equal("Finance", adminEnv.Data.Scope);
        Assert.Equal("Finance", supplierEnv.Data.Scope);
    }

    [Fact]
    public async Task ShutdownCancellation_PropagatesException()
    {
        var testClients = new TestHubClients
        {
            GroupHandler = (group, method, args) => Task.CompletedTask,
            UserHandler = (users, method, args) => Task.CompletedTask
        };
        var hubContext = new TestHubContext(testClients);
        var options = Options.Create(new AnalyticsHubOptions { SendTimeoutSeconds = 1 });
        var notifier = new AnalyticsHubNotifier(hubContext, options, TimeProvider.System, NullLogger<AnalyticsHubNotifier>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await notifier.NotifyDataChangedAsync(AnalyticsDataScopes.Finance, [SupplierId], cts.Token));
    }

    private sealed class TestHubContext(IHubClients clients) : IHubContext<AnalyticsHub>
    {
        public IHubClients Clients { get; } = clients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class TestHubClients : IHubClients
    {
        public Func<string, string, object?[], Task>? GroupHandler { get; set; }
        public Func<IReadOnlyList<string>, string, object?[], Task>? UserHandler { get; set; }

        public List<(string Group, string Method, object?[] Args)> GroupInvocations { get; } = [];
        public List<(IReadOnlyList<string> Users, string Method, object?[] Args)> UserInvocations { get; } = [];

        public IClientProxy Group(string groupName) => new TestClientProxy((method, args, ct) =>
        {
            GroupInvocations.Add((groupName, method, args));
            return GroupHandler?.Invoke(groupName, method, args) ?? Task.CompletedTask;
        });

        public IClientProxy Users(IReadOnlyList<string> userIds) => new TestClientProxy((method, args, ct) =>
        {
            UserInvocations.Add((userIds, method, args));
            return UserHandler?.Invoke(userIds, method, args) ?? Task.CompletedTask;
        });

        public IClientProxy All => throw new NotImplementedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IClientProxy Client(string connectionId) => throw new NotImplementedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IClientProxy User(string userId) => Users([userId]);
    }

    private sealed class TestClientProxy(Func<string, object?[], CancellationToken, Task> sender) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return sender(method, args, cancellationToken);
        }
    }
}
