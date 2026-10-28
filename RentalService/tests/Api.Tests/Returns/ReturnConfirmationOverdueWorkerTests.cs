using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnConfirmationOverdueWorkerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset RequestSubmittedAt = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
    private string _connection = "";
    private string _database = "";
    private readonly FakeTimeProvider _clock = new(RequestSubmittedAt);

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

    private ReturnConfirmationOverdueService CreateService(
        RentalServiceDbContext db,
        IRentalEventProducer? producer = null,
        IOrderHubNotifier? notifier = null)
    {
        var outbox = new OutboxRepository(db);
        var eventProducer = producer ?? new FakeRentalEventProducer();
        return new ReturnConfirmationOverdueService(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
            db,
            eventProducer,
            _clock,
            notifier,
            NullLogger<ReturnConfirmationOverdueService>.Instance);
    }

    private async Task<(RentalOrder order, RentalItem item, ReturnRequest returnReq)> SeedOrderWithReturnRequestAsync(int quantity = 2)
    {
        var order = ReturnTests.NewOrder(quantity);
        var item = order.Items.Single();
        Guid reqId = Guid.NewGuid();
        var units = item.UnitAssignments.Select(u =>
            new ReturnRequestUnit(Guid.NewGuid(), reqId, u.Id, [])).ToList();
        var returnReq = new ReturnRequest(reqId, item.Id, RequestSubmittedAt, "Return note", order.RenterId, RequestSubmittedAt, units);
        item.AddReturnRequest(returnReq);

        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, item, returnReq);
    }

    [Fact]
    public async Task OverdueScan_Before24Hours_DoesNotFlagOrWarn()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);

        // Advance clock to 23 hours 59 minutes (before 24h deadline)
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(23).AddMinutes(59));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var service = CreateService(db, fakeProducer);

        var dueOrderIds = await service.GetDueOrderIdsAsync(100);
        Assert.Empty(dueOrderIds);
        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsSuccess);
        Assert.Equal(0, warned.Value);
        Assert.Empty(fakeProducer.ReturnConfirmationOverdueEvents);

        await using var verifyDb = NewDb();
        var units = await verifyDb.ReturnRequestUnits.ToListAsync();
        Assert.All(units, u => Assert.Null(u.OverdueNotifiedAt));
    }

    [Fact]
    public async Task OverdueScan_AtAndAfter24Hours_FlagsActionableUnitsAndEnqueuesEvent()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);

        // Advance clock to exactly 24 hours
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var mockNotifier = new Mock<IOrderHubNotifier>();
        var service = CreateService(db, fakeProducer, mockNotifier.Object);

        var dueOrderIds = await service.GetDueOrderIdsAsync(100);
        Assert.Single(dueOrderIds);
        Assert.Equal(order.Id, dueOrderIds[0]);

        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsSuccess);
        Assert.Equal(2, warned.Value);
        // Verify outbox event enqueued with correct payload
        Assert.Single(fakeProducer.ReturnConfirmationOverdueEvents);
        var overdueEvent = fakeProducer.ReturnConfirmationOverdueEvents[0];
        Assert.Equal(order.Id, overdueEvent.RentalOrderId);
        Assert.Equal(item.Id, overdueEvent.RentalItemId);
        Assert.Equal(returnReq.Id, overdueEvent.ReturnRequestId);
        Assert.Equal(item.SupplierId, overdueEvent.SupplierId);
        Assert.Equal(order.RenterId, overdueEvent.RenterId);
        Assert.Equal(2, overdueEvent.OverdueAssignmentIds.Count);

        // Verify database: OverdueNotifiedAt set, no ReturnRecord created, item status still InRental
        await using var verifyDb = NewDb();
        var reloadedOrder = await verifyDb.RentalOrders
            .Include(o => o.Items).ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .Include(o => o.Items).ThenInclude(i => i.ReturnRecords)
            .SingleAsync(o => o.Id == order.Id);

        var reloadedItem = reloadedOrder.Items.Single();
        Assert.Equal(RentalItemStatus.InRental, reloadedItem.Status);
        Assert.Empty(reloadedItem.ReturnRecords);
        Assert.All(reloadedItem.ReturnRequests.Single().Units, u => Assert.NotNull(u.OverdueNotifiedAt));

        // Verify notification was sent to both Renter and Supplier
        mockNotifier.Verify(n => n.NotifyRenterOrderChangedAsync(
            order.RenterId,
            It.Is<RenterOrderChangedNotification>(notif => notif.ChangeType == OrderChangeTypes.ReturnConfirmationOverdue),
            It.IsAny<CancellationToken>()), Times.Once);

        mockNotifier.Verify(n => n.NotifySupplierSubOrderChangedAsync(
            item.SupplierId,
            It.Is<SupplierSubOrderChangedNotification>(notif => notif.ChangeType == OrderChangeTypes.ReturnConfirmationOverdue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OverdueScan_PartialConfirmation_OnlyFlagsAndWarnsUnconfirmedUnits()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var confirmedUnit = returnReq.Units.First();
        var unconfirmedUnit = returnReq.Units.Last();

        // Unit 1 is confirmed before deadline
        await using (var dbClose = NewDb())
        {
            var u1 = await dbClose.ReturnRequestUnits.SingleAsync(u => u.Id == confirmedUnit.Id);
            u1.SetClosedAt(RequestSubmittedAt.AddHours(12));
            await dbClose.SaveChangesAsync();
        }

        // Advance to 25 hours
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(25));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var service = CreateService(db, fakeProducer);

        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsSuccess);
        Assert.Equal(1, warned.Value);
        Assert.Single(fakeProducer.ReturnConfirmationOverdueEvents);
        var payload = fakeProducer.ReturnConfirmationOverdueEvents[0];
        Assert.Single(payload.OverdueAssignmentIds);
        Assert.Equal(unconfirmedUnit.RentalUnitAssignmentId, payload.OverdueAssignmentIds[0]);

        await using var verifyDb = NewDb();
        var units = await verifyDb.ReturnRequestUnits.ToDictionaryAsync(u => u.Id);
        Assert.Null(units[confirmedUnit.Id].OverdueNotifiedAt);
        Assert.NotNull(units[unconfirmedUnit.Id].OverdueNotifiedAt);
    }

    [Fact]
    public async Task OverdueScan_DisputedUnit_CanStillBeOverdue()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var unit = returnReq.Units.Single();

        // Renter reported an issue on this unit
        await using (var dbIssue = NewDb())
        {
            var req = await dbIssue.ReturnRequests.Include(r => r.Issues).SingleAsync(r => r.Id == returnReq.Id);
            req.AddIssue(new ReturnRequestIssue(
                Guid.NewGuid(), returnReq.Id, order.RenterId, ActorRole.Renter,
                "Issue reported", null, null, RequestSubmittedAt.AddHours(10), [unit.RentalUnitAssignmentId]));
            await dbIssue.SaveChangesAsync();
        }

        // Advance to 24 hours
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var service = CreateService(db, fakeProducer);

        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsSuccess);
        Assert.Equal(1, warned.Value);
    }

    [Fact]
    public async Task OverdueScan_FullyResolvedRequest_NotWarned()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var unit1 = returnReq.Units.First();
        var unit2 = returnReq.Units.Last();

        // Unit 1 confirmed, Unit 2 rejected
        await using (var dbClose = NewDb())
        {
            var u1 = await dbClose.ReturnRequestUnits.SingleAsync(u => u.Id == unit1.Id);
            u1.SetClosedAt(RequestSubmittedAt.AddHours(5));
            var u2 = await dbClose.ReturnRequestUnits.SingleAsync(u => u.Id == unit2.Id);
            u2.SetRejectedAt(RequestSubmittedAt.AddHours(6));
            await dbClose.SaveChangesAsync();
        }

        // Advance to 26 hours
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(26));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var service = CreateService(db, fakeProducer);

        var dueOrderIds = await service.GetDueOrderIdsAsync(100);
        Assert.Empty(dueOrderIds);

        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsSuccess);
        Assert.Equal(0, warned.Value);
    }

    [Fact]
    public async Task OverdueScan_MultipleRuns_DoesNotDuplicateWarnings()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        await using var db = NewDb();
        var fakeProducer = new FakeRentalEventProducer();
        var service = CreateService(db, fakeProducer);

        // Run 1
        var warned1 = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned1.IsSuccess);
        Assert.Equal(2, warned1.Value);
        Assert.Single(fakeProducer.ReturnConfirmationOverdueEvents);

        // Run 2 immediately after
        var warned2 = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned2.IsSuccess);
        Assert.Equal(0, warned2.Value);

        // Server-side query returns empty
        var dueOrderIds = await service.GetDueOrderIdsAsync(100);
        Assert.Empty(dueOrderIds);
    }

    [Fact]
    public async Task OverdueScan_EnqueueFailure_RollsBackWarningFlag()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        var failingProducerMock = new Mock<IRentalEventProducer>();
        failingProducerMock.Setup(p => p.EnqueueReturnConfirmationOverdueAsync(
            It.IsAny<ReturnConfirmationOverduePayload>(),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.Failure("KAFKA_ERROR", "Failed to enqueue outbox event."));

        await using var db = NewDb();
        var service = CreateService(db, failingProducerMock.Object);

        var warned = await service.ProcessOrderAsync(order.Id);
        Assert.True(warned.IsFailure);
        Assert.Equal("KAFKA_ERROR", warned.Error.Code);
        // Verify rollback: unit OverdueNotifiedAt is STILL NULL!
        await using var verifyDb = NewDb();
        var unit = await verifyDb.ReturnRequestUnits.SingleAsync();
        Assert.Null(unit.OverdueNotifiedAt);

        // Which means next run can retry
        var dueOrderIds = await service.GetDueOrderIdsAsync(100);
        Assert.Single(dueOrderIds);
    }

    [Fact]
    public async Task OverdueScan_TwoRequestsSameOrder_SecondEnqueueFails_RollsBackEntireOrderAndNoFlagsPersisted()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        Guid req1Id = Guid.NewGuid();
        Guid req2Id = Guid.NewGuid();
        var req1 = new ReturnRequest(req1Id, item.Id, RequestSubmittedAt, "Note 1", order.RenterId, RequestSubmittedAt,
            [new ReturnRequestUnit(Guid.NewGuid(), req1Id, u1.Id, [])]);
        var req2 = new ReturnRequest(req2Id, item.Id, RequestSubmittedAt, "Note 2", order.RenterId, RequestSubmittedAt,
            [new ReturnRequestUnit(Guid.NewGuid(), req2Id, u2.Id, [])]);
        item.AddReturnRequest(req1);
        item.AddReturnRequest(req2);

        await using (var dbInit = NewDb())
        {
            dbInit.Add(order);
            await dbInit.SaveChangesAsync();
        }

        long initialVersion = order.Version;
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        int callCount = 0;
        var mockProducer = new Mock<IRentalEventProducer>();
        mockProducer.Setup(p => p.EnqueueReturnConfirmationOverdueAsync(
            It.IsAny<ReturnConfirmationOverduePayload>(),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Result.Success(new ProducerEnqueueResult(Guid.NewGuid(), false, "ReturnConfirmationOverdue", "topic", "key"));
                }
                return Error.Failure("KAFKA_FAIL_SECOND", "Simulated failure on 2nd request");
            });

        var mockNotifier = new Mock<IOrderHubNotifier>();

        await using var db = NewDb();
        var service = CreateService(db, mockProducer.Object, mockNotifier.Object);

        var result = await service.ProcessOrderAsync(order.Id);
        Assert.True(result.IsFailure);
        Assert.Equal("KAFKA_FAIL_SECOND", result.Error.Code);

        // Notifications must NOT be sent on rollback
        mockNotifier.Verify(n => n.NotifyRenterOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<RenterOrderChangedNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        mockNotifier.Verify(n => n.NotifySupplierSubOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<SupplierSubOrderChangedNotification>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify entire order in DB: NO flags persisted, version NOT changed, no outbox messages
        await using var verifyDb = NewDb();
        var reloadedOrder = await verifyDb.RentalOrders
            .Include(o => o.Items).ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(o => o.Id == order.Id);

        Assert.Equal(initialVersion, reloadedOrder.Version);
        var allUnits = reloadedOrder.Items.Single().ReturnRequests.SelectMany(r => r.Units).ToList();
        Assert.Equal(2, allUnits.Count);
        Assert.All(allUnits, u => Assert.Null(u.OverdueNotifiedAt));

        var outboxCount = await verifyDb.RentalOutboxMessages.CountAsync();
        Assert.Equal(0, outboxCount);
    }

    [Fact]
    public async Task OverdueScan_RetryAfterFailure_WarnsAllDueUnitsAndIncrementsVersionOnce()
    {
        var order = ReturnTests.NewOrder(2);
        var item = order.Items.Single();
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        Guid req1Id = Guid.NewGuid();
        Guid req2Id = Guid.NewGuid();
        var req1 = new ReturnRequest(req1Id, item.Id, RequestSubmittedAt, "Note 1", order.RenterId, RequestSubmittedAt,
            [new ReturnRequestUnit(Guid.NewGuid(), req1Id, u1.Id, [])]);
        var req2 = new ReturnRequest(req2Id, item.Id, RequestSubmittedAt, "Note 2", order.RenterId, RequestSubmittedAt,
            [new ReturnRequestUnit(Guid.NewGuid(), req2Id, u2.Id, [])]);
        item.AddReturnRequest(req1);
        item.AddReturnRequest(req2);

        await using (var dbInit = NewDb())
        {
            dbInit.Add(order);
            await dbInit.SaveChangesAsync();
        }

        long initialVersion = order.Version;
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        // Step 1: run with failing producer -> failure
        var failingProducerMock = new Mock<IRentalEventProducer>();
        failingProducerMock.Setup(p => p.EnqueueReturnConfirmationOverdueAsync(
            It.IsAny<ReturnConfirmationOverduePayload>(),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.Failure("KAFKA_ERROR", "Broker down"));

        await using (var db1 = NewDb())
        {
            var service1 = CreateService(db1, failingProducerMock.Object);
            var failRes = await service1.ProcessOrderAsync(order.Id);
            Assert.True(failRes.IsFailure);
        }

        // Step 2: retry with working producer -> success
        var workingProducer = new FakeRentalEventProducer();
        var mockNotifier = new Mock<IOrderHubNotifier>();

        await using (var db2 = NewDb())
        {
            var service2 = CreateService(db2, workingProducer, mockNotifier.Object);
            var successRes = await service2.ProcessOrderAsync(order.Id);
            Assert.True(successRes.IsSuccess);
            Assert.Equal(2, successRes.Value);
        }

        // Verify DB: version incremented by 1, both units flagged
        await using (var verifyDb = NewDb())
        {
            var reloadedOrder = await verifyDb.RentalOrders
                .Include(o => o.Items).ThenInclude(i => i.ReturnRequests).ThenInclude(r => r.Units)
                .SingleAsync(o => o.Id == order.Id);

            Assert.Equal(initialVersion + 1, reloadedOrder.Version);
            var allUnits = reloadedOrder.Items.Single().ReturnRequests.SelectMany(r => r.Units).ToList();
            Assert.All(allUnits, u => Assert.NotNull(u.OverdueNotifiedAt));
        }

        // Verify exactly 1 notification to Renter and 1 to Supplier
        mockNotifier.Verify(n => n.NotifyRenterOrderChangedAsync(
            order.RenterId,
            It.Is<RenterOrderChangedNotification>(notif => notif.ChangeType == OrderChangeTypes.ReturnConfirmationOverdue),
            It.IsAny<CancellationToken>()), Times.Once);
        mockNotifier.Verify(n => n.NotifySupplierSubOrderChangedAsync(
            item.SupplierId,
            It.Is<SupplierSubOrderChangedNotification>(notif => notif.ChangeType == OrderChangeTypes.ReturnConfirmationOverdue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenOrderFails_CountsFailedAndNotProcessed()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        var failingProducerMock = new Mock<IRentalEventProducer>();
        failingProducerMock.Setup(p => p.EnqueueReturnConfirmationOverdueAsync(
            It.IsAny<ReturnConfirmationOverduePayload>(),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.Failure("KAFKA_ERROR", "Broker down"));

        await using var db = NewDb();
        var service = CreateService(db, failingProducerMock.Object);

        var batchRes = await service.ProcessBatchAsync(100);
        Assert.Equal(1, batchRes.FailedOrders);
        Assert.Equal(0, batchRes.ProcessedOrders);
        Assert.Equal(0, batchRes.WarnedUnits);
    }

    [Fact]
    public async Task ProcessBatchAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        _clock.SetUtcNow(RequestSubmittedAt.AddHours(24));

        await using var db = NewDb();
        var service = CreateService(db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessBatchAsync(100, cts.Token));
    }
}
