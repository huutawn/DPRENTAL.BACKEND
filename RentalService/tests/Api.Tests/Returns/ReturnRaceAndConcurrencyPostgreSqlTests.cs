using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Security.Jwt;
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
public sealed class ReturnRaceAndConcurrencyPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
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
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _connection,
            ["Kafka:Enabled"] = "true"
        }).Build();

    private ReturnService CreateService(RentalServiceDbContext db)
    {
        var outbox = new OutboxRepository(db);
        var settings = Configuration();
        var invCommands = new InventoryCommandProducer(
            outbox, Options.Create(new KafkaOptions { Enabled = true }), _clock);
        var events = new RentalEventProducer(
            new KafkaOutboxWriter(outbox),
            Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = KafkaContractSpec.Topics.RentalEvents }),
            _clock);

        return new ReturnService(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(settings, NullLogger<PostgresOrderLockManager>.Instance),
            db,
            Mock.Of<Application.Clients.IRentalMediaClient>(),
            _clock,
            events,
            settings,
            invCommands,
            null,
            NullLogger<ReturnService>.Instance);
    }

    private ReturnConfirmationOverdueService CreateOverdueService(RentalServiceDbContext db)
    {
        var outbox = new OutboxRepository(db);
        var events = new RentalEventProducer(
            new KafkaOutboxWriter(outbox),
            Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = KafkaContractSpec.Topics.RentalEvents }),
            _clock);

        return new ReturnConfirmationOverdueService(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
            db,
            events,
            _clock,
            null,
            NullLogger<ReturnConfirmationOverdueService>.Instance);
    }

    private async Task<(RentalOrder order, RentalItem item, ReturnRequest returnReq)> SeedOrderWithReturnRequestAsync(int quantity = 2)
    {
        var order = ReturnTests.NewOrder(quantity);
        var item = order.Items.Single();
        Guid reqId = Guid.NewGuid();
        var units = item.UnitAssignments.Select(u =>
            new ReturnRequestUnit(Guid.NewGuid(), reqId, u.Id, [])).ToList();
        var returnReq = new ReturnRequest(reqId, item.Id, Now.AddHours(-2), "Return note", order.RenterId, Now.AddHours(-2), units);
        item.AddReturnRequest(returnReq);

        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, item, returnReq);
    }

    [Fact]
    public async Task Race_ReportIssue_And_SupplierConfirm_SerializesWithoutConflictOrLoss()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        var barrier = new SemaphoreSlim(0, 2);

        var reportTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(order.RenterId);
            return await service.ReportReturnIssueAsync(item.Id, returnReq.Id, caller,
                new ReportReturnIssueRequest("Dispute reason", "Dispute note", null, [u1.Id]),
                "race-report-key");
        });

        var confirmTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(item.SupplierId);
            return await service.RecordReturnAsync(item.Id, caller,
                new RecordReturnRequest(Now, "Supplier physically confirmed",
                    [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, null, [])]),
                "race-confirm-key");
        });

        barrier.Release(2);
        await Task.WhenAll(reportTask, confirmTask);

        var reportResult = await reportTask;
        var confirmResult = await confirmTask;

        // Both operations must finish with valid sequential states
        Assert.True(confirmResult.IsSuccess);

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Issues)
            .SingleAsync(i => i.Id == item.Id);

        // Exactly 1 ReturnRecord for u1
        Assert.Single(reloadedItem.ReturnRecords);
        var reqUnit = reloadedItem.ReturnRequests.Single().Units.Single(u => u.RentalUnitAssignmentId == u1.Id);
        Assert.NotNull(reqUnit.ClosedAt);

        // If report succeeded, issue history is preserved; if confirm ran first, report either succeeded or reported against closed
        if (reportResult.IsSuccess)
        {
            Assert.Single(reloadedItem.ReturnRequests.Single().Issues);
        }
    }

    [Fact]
    public async Task Race_WorkerOverdue_And_SupplierConfirm_SerializesCleanly()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        // Advance clock past 24h deadline
        _clock.SetUtcNow(Now.AddHours(25));

        var barrier = new SemaphoreSlim(0, 2);

        var workerTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var overdueService = CreateOverdueService(db);
            return await overdueService.ProcessOrderAsync(order.Id);
        });

        var supplierTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(item.SupplierId);
            return await service.RecordReturnAsync(item.Id, caller,
                new RecordReturnRequest(Now.AddHours(25), "Confirmed return",
                    [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, null, [])]),
                "race-supplier-worker-key");
        });

        barrier.Release(2);
        await Task.WhenAll(workerTask, supplierTask);

        var supplierResult = await supplierTask;
        Assert.True(supplierResult.IsSuccess);

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        // Exactly 1 ReturnRecord
        Assert.Single(reloadedItem.ReturnRecords);
    }

    [Fact]
    public async Task Race_AdminResolve_And_SupplierConfirm_CreatesSingleReturnRecord()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        var barrier = new SemaphoreSlim(0, 2);

        var adminTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, [RentalAuthClaims.StandardRoles.Admin]);
            return await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, caller,
                new AdminResolveReturnRequest(
                    ReturnResolutionDecision.ConfirmReceived,
                    "Admin confirmed",
                    [u1.Id],
                    ActualReturnAt: Now.AddHours(-1),
                    Units: [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, null, [])]),
                "race-admin-resolve-key");
        });

        var supplierTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(item.SupplierId);
            return await service.RecordReturnAsync(item.Id, caller,
                new RecordReturnRequest(Now, "Supplier return",
                    [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, null, [])]),
                "race-supplier-confirm-key");
        });

        barrier.Release(2);
        await Task.WhenAll(adminTask, supplierTask);

        var adminResult = await adminTask;
        var supplierResult = await supplierTask;

        // Exactly one of them should succeed to return the machine, or one succeeded and the other received AssetAlreadyReturned
        Assert.True(adminResult.IsSuccess || supplierResult.IsSuccess);

        await using var verifyDb = NewDb();
        var returnRecords = await verifyDb.ReturnRecords.Where(r => r.RentalItemId == item.Id).ToListAsync();
        Assert.Single(returnRecords); // Never duplicate ReturnRecord!
    }

    [Fact]
    public async Task Race_TwoAdmins_ResolveSameUnit_AppliesExactlyOneDecision()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        var barrier = new SemaphoreSlim(0, 2);

        var admin1Task = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, [RentalAuthClaims.StandardRoles.Admin]);
            return await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, caller,
                new AdminResolveReturnRequest(
                    ReturnResolutionDecision.ConfirmReceived,
                    "Admin 1 confirmed",
                    [u1.Id],
                    ActualReturnAt: Now.AddHours(-1),
                    Units: [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, null, [])]),
                "race-admin1-key");
        });

        var admin2Task = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = CreateService(db);
            var caller = RentalCallerContext.ForUser(Guid.NewGuid(), true, [RentalAuthClaims.StandardRoles.Admin]);
            return await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, caller,
                new AdminResolveReturnRequest(
                    ReturnResolutionDecision.RejectRequest,
                    "Admin 2 rejected",
                    [u1.Id]),
                "race-admin2-key");
        });

        barrier.Release(2);
        await Task.WhenAll(admin1Task, admin2Task);

        var res1 = await admin1Task;
        var res2 = await admin2Task;

        // Exactly one must succeed, the other must fail because unit is already closed or rejected!
        Assert.True(res1.IsSuccess ^ res2.IsSuccess);

        await using var verifyDb = NewDb();
        var reloadedReq = await verifyDb.ReturnRequests
            .Include(r => r.Resolutions)
            .SingleAsync(r => r.Id == returnReq.Id);

        Assert.Single(reloadedReq.Resolutions);
    }

    [Fact]
    public async Task Race_RejectRequest_And_RenterCreateNewRequest_PermitsCleanSequence()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var u1 = item.UnitAssignments.ElementAt(0);

        // Admin rejects unit 1
        var adminCaller = RentalCallerContext.ForUser(Guid.NewGuid(), true, [RentalAuthClaims.StandardRoles.Admin]);
        await using (var db = NewDb())
        {
            var service = CreateService(db);
            var rejectResult = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller,
                new AdminResolveReturnRequest(ReturnResolutionDecision.RejectRequest, "Rejected unit", [u1.Id]),
                "reject-race-key");
            Assert.True(rejectResult.IsSuccess);
        }

        // Immediately after, Renter creates a new return request for the same unit
        await using (var db2 = NewDb())
        {
            var service2 = CreateService(db2);
            var renterCaller = RentalCallerContext.ForUser(order.RenterId);
            var newReq = new CreateReturnRequest(Now, "New request after reject", [new ReturnRequestUnitDto(u1.Id, [])]);
            var newReqResult = await service2.CreateReturnRequestAsync(item.Id, renterCaller, newReq, "new-req-after-reject-key");
            Assert.True(newReqResult.IsSuccess);
        }

        await using var verifyDb = NewDb();
        var requestUnits = await verifyDb.ReturnRequestUnits
            .Where(u => u.RentalUnitAssignmentId == u1.Id)
            .OrderBy(u => u.RejectedAt)
            .ToListAsync();

        Assert.Equal(2, requestUnits.Count);
        Assert.Contains(requestUnits, u => u.RejectedAt != null);
        Assert.Contains(requestUnits, u => u.RejectedAt == null && u.ClosedAt == null);
    }
}
