using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnRequestNotificationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 11, 0, 0, TimeSpan.Zero);
    private string _connection = "";
    private string _database = "";
    private readonly FakeTimeProvider _clock = new(Now);

    public async Task InitializeAsync()
    {
        (_connection, _database) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(PostgresTestDatabaseHelper.BaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private RentalServiceDbContext NewDb() => new(PostgresTestDatabaseHelper.CreateOptions(_connection));

    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _connection }).Build();

    private ReturnService Service(RentalServiceDbContext db,
        IOrderHubNotifier? notifier = null,
        ILogger<ReturnService>? logger = null)
    {
        var outbox = new OutboxRepository(db);
        var settings = Configuration();
        var inventoryCommands = new InventoryCommandProducer(
            outbox, Options.Create(new KafkaOptions { Enabled = true }), _clock);
        return new(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
            db,
            Mock.Of<IRentalMediaClient>(),
            _clock,
            new RentalEventProducer(new KafkaOutboxWriter(outbox), Options.Create(new KafkaOptions
            {
                Enabled = true,
                RentalEventsTopic = settings["Kafka:RentalEventsTopic"] ?? KafkaContractSpec.Topics.RentalEvents
            }), _clock),
            settings,
            inventoryCommands,
            notifier,
            logger);
    }

    private async Task<(RentalOrder order, RentalItem item)> SeedOrderAsync(int quantity = 2)
    {
        var order = ReturnTests.NewOrder(quantity);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.PaymentConfirmedAt))!.SetValue(order, Now.AddDays(-2));
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, order.Items.Single());
    }

    [Fact]
    public async Task CreateReturnRequest_SendsReturnRequestedNotification_ToRenterAndSupplier()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        var notifierMock = new Mock<IOrderHubNotifier>();
        RenterOrderChangedNotification? renterNotification = null;
        SupplierSubOrderChangedNotification? supplierNotification = null;

        notifierMock
            .Setup(n => n.NotifyRenterOrderChangedAsync(order.RenterId, It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, RenterOrderChangedNotification, CancellationToken>((_, n, _) => renterNotification = n)
            .Returns(Task.CompletedTask);

        notifierMock
            .Setup(n => n.NotifySupplierSubOrderChangedAsync(item.SupplierId, It.IsAny<SupplierSubOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SupplierSubOrderChangedNotification, CancellationToken>((_, n, _) => supplierNotification = n)
            .Returns(Task.CompletedTask);

        await using var db = NewDb();
        var service = Service(db, notifier: notifierMock.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        var request = new CreateReturnRequest(Now.AddHours(-1), "Notification test note",
            [new ReturnRequestUnitDto(u1.Id, [])]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, "op-notify-1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(renterNotification);
        Assert.Equal(order.Id, renterNotification.OrderId);
        Assert.Equal(OrderChangeTypes.ReturnRequested, renterNotification.ChangeType);
        Assert.Equal(Now, renterNotification.OccurredAt);

        Assert.NotNull(supplierNotification);
        Assert.Equal(order.Id, supplierNotification.OrderId);
        Assert.Equal(order.SubOrders.First().Id, supplierNotification.SubOrderId);
        Assert.Equal(OrderChangeTypes.ReturnRequested, supplierNotification.ChangeType);
        Assert.Equal(Now, supplierNotification.OccurredAt);
    }

    [Fact]
    public async Task ReplayReturnRequest_DoesNotSendNotification_OrIncrementVersion()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        var notifierMock = new Mock<IOrderHubNotifier>();
        notifierMock
            .Setup(n => n.NotifyRenterOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        notifierMock
            .Setup(n => n.NotifySupplierSubOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<SupplierSubOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var db1 = NewDb();
        var service1 = Service(db1, notifier: notifierMock.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(Now.AddHours(-1), "Replay note", [new ReturnRequestUnitDto(u1.Id, [])]);

        var initialResp = await service1.CreateReturnRequestAsync(item.Id, caller, request, "op-notify-replay");
        Assert.True(initialResp.IsSuccess);

        // Reset invocations
        notifierMock.Invocations.Clear();

        await using var verifyDb1 = NewDb();
        var orderAfterFirst = await verifyDb1.RentalOrders.SingleAsync(o => o.Id == order.Id);
        var versionAfterFirst = orderAfterFirst.Version;

        // Replay with same key
        await using var db2 = NewDb();
        var service2 = Service(db2, notifier: notifierMock.Object);
        var replayResp = await service2.CreateReturnRequestAsync(item.Id, caller, request, "op-notify-replay");

        Assert.True(replayResp.IsSuccess);
        Assert.Equal(initialResp.Value.ReturnRequestId, replayResp.Value.ReturnRequestId);

        // Verifications: NOTIFIER WAS NOT CALLED ON REPLAY!
        notifierMock.Verify(
            n => n.NotifyRenterOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);
        notifierMock.Verify(
            n => n.NotifySupplierSubOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<SupplierSubOrderChangedNotification>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Version was NOT incremented on replay
        await using var verifyDb2 = NewDb();
        var orderAfterReplay = await verifyDb2.RentalOrders.SingleAsync(o => o.Id == order.Id);
        Assert.Equal(versionAfterFirst, orderAfterReplay.Version);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotFailCommittedReturnRequest()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        var notifierMock = new Mock<IOrderHubNotifier>();
        notifierMock
            .Setup(n => n.NotifyRenterOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SignalR hub connection lost"));

        await using var db = NewDb();
        var service = Service(db, notifier: notifierMock.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(Now.AddHours(-1), "Failure note", [new ReturnRequestUnitDto(u1.Id, [])]);

        // Creating return request still succeeds!
        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, "op-notify-err");
        Assert.True(result.IsSuccess);

        // And it was committed in DB
        await using var verifyDb = NewDb();
        var savedItem = await verifyDb.RentalItems.Include(i => i.ReturnRequests).SingleAsync(i => i.Id == item.Id);
        Assert.Single(savedItem.ReturnRequests);
    }
}
