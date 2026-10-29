using System.Net;
using System.Net.Http.Headers;
using Dprental.RentalService.Api.Tests.Orders;
using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Api.Tests.Cart;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnClosingAndConcurrencyPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
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
        IInventoryCommandProducer? inventoryCommands = null,
        IRentalMediaClient? media = null,
        IConfiguration? configuration = null)
    {
        var outbox = new OutboxRepository(db);
        IConfiguration settings = configuration ?? Configuration();
        var invCommands = inventoryCommands ?? new InventoryCommandProducer(
            outbox, Options.Create(new KafkaOptions { Enabled = true }), _clock);
        return new(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
            db,
            media ?? Mock.Of<IRentalMediaClient>(),
            _clock,
            new RentalEventProducer(new KafkaOutboxWriter(outbox), Options.Create(new KafkaOptions
            {
                Enabled = true,
                RentalEventsTopic = settings["Kafka:RentalEventsTopic"] ?? KafkaContractSpec.Topics.RentalEvents
            }), _clock),
            settings,
            invCommands);
    }

    private async Task<(RentalOrder order, RentalItem item)> SeedOrderAsync(int quantity = 3)
    {
        var order = ReturnTests.NewOrder(quantity);
        typeof(RentalOrder).GetProperty(nameof(RentalOrder.PaymentConfirmedAt))!.SetValue(order, Now.AddDays(-2));
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, order.Items.Single());
    }

    [Fact]
    public async Task PartialReturn_ClosesOnlyReturnedUnits_LeavesOtherRequestUnitsOpen()
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        await using var db1 = NewDb();
        var service1 = Service(db1);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        // Renter creates return request for u1 and u2
        var createReq = new CreateReturnRequest(Now.AddHours(-1), "Returning 2 of 3 units",
            [new ReturnRequestUnitDto(u1.Id, []), new ReturnRequestUnitDto(u2.Id, [])]);
        var reqResult = await service1.CreateReturnRequestAsync(item.Id, renterCaller, createReq, "op-req-part");
        Assert.True(reqResult.IsSuccess);

        // Supplier returns ONLY u1
        _clock.SetUtcNow(Now.AddMinutes(15));
        await using var db2 = NewDb();
        var service2 = Service(db2);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);

        var returnReq = new RecordReturnRequest(Now.AddMinutes(10), "Supplier returned u1",
            [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "Good", [])]);
        var returnResult = await service2.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-u1");
        Assert.True(returnResult.IsSuccess);

        // Verify in DB: u1 request unit is closed with now, u2 is still open
        await using var verifyDb1 = NewDb();
        var loadedItem = await verifyDb1.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .Include(i => i.ReturnRecords)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(RentalItemStatus.InRental, loadedItem.Status);
        Assert.Single(loadedItem.ReturnRecords);

        var savedUnits = loadedItem.ReturnRequests.Single().Units.ToDictionary(u => u.RentalUnitAssignmentId);
        Assert.Equal(Now.AddMinutes(15), savedUnits[u1.Id].ClosedAt);
        Assert.Null(savedUnits[u2.Id].ClosedAt);

        // Later Supplier returns u2
        _clock.SetUtcNow(Now.AddMinutes(30));
        await using var db3 = NewDb();
        var service3 = Service(db3);
        var returnReq2 = new RecordReturnRequest(Now.AddMinutes(25), "Supplier returned u2",
            [new ReturnUnitRequest(u2.Id, UnitCondition.Normal, "Good", [])]);
        var returnResult2 = await service3.RecordReturnAsync(item.Id, supplierCaller, returnReq2, "op-ret-u2");
        Assert.True(returnResult2.IsSuccess);

        // Verify both units are now closed
        await using var verifyDb2 = NewDb();
        var reloadedItem = await verifyDb2.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        var finalUnits = reloadedItem.ReturnRequests.Single().Units.ToDictionary(u => u.RentalUnitAssignmentId);
        Assert.Equal(Now.AddMinutes(15), finalUnits[u1.Id].ClosedAt);
        Assert.Equal(Now.AddMinutes(30), finalUnits[u2.Id].ClosedAt);
    }

    [Fact]
    public async Task ReturnMultipleUnits_ClosesUnitsAcrossMultipleRequests()
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        await using var db1 = NewDb();
        var service1 = Service(db1);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        // Request 1 for u1
        var req1 = await service1.CreateReturnRequestAsync(item.Id, renterCaller,
            new CreateReturnRequest(Now.AddHours(-2), "Req 1", [new ReturnRequestUnitDto(u1.Id, [])]), "op-req-1");
        Assert.True(req1.IsSuccess);

        // Request 2 for u2
        var req2 = await service1.CreateReturnRequestAsync(item.Id, renterCaller,
            new CreateReturnRequest(Now.AddHours(-1), "Req 2", [new ReturnRequestUnitDto(u2.Id, [])]), "op-req-2");
        Assert.True(req2.IsSuccess);

        // Supplier returns both u1 and u2 in one call
        var closeTime = Now.AddMinutes(20);
        _clock.SetUtcNow(closeTime);
        await using var db2 = NewDb();
        var service2 = Service(db2);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);

        var returnReq = new RecordReturnRequest(Now.AddMinutes(15), "Returning both",
            [
                new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "OK", []),
                new ReturnUnitRequest(u2.Id, UnitCondition.Normal, "OK", [])
            ]);
        var returnRes = await service2.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-multi");
        Assert.True(returnRes.IsSuccess);

        // Verify in DB: units across both requests are closed with closeTime
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(2, reloadedItem.ReturnRequests.Count);
        foreach (var req in reloadedItem.ReturnRequests)
        {
            Assert.Single(req.Units);
            Assert.Equal(closeTime, req.Units.Single().ClosedAt);
        }
    }

    [Fact]
    public async Task ReturnUnit_WithoutPriorRequest_SucceedsCleanly()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        // No return request created. Supplier directly records return.
        await using var db = NewDb();
        var service = Service(db);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);

        var returnReq = new RecordReturnRequest(Now.AddMinutes(-5), "Direct return without request",
            [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "OK", [])]);
        var result = await service.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-direct");
        Assert.True(result.IsSuccess);

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Single(reloadedItem.ReturnRecords);
        Assert.Empty(reloadedItem.ReturnRequests);
    }

    [Fact]
    public async Task Rollback_WhenInventoryEnqueueFails_DoesNotPersistReturnRecordOrClosedAt()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        await using var db1 = NewDb();
        var service1 = Service(db1);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        // Create return request for u1
        var req = await service1.CreateReturnRequestAsync(item.Id, renterCaller,
            new CreateReturnRequest(Now.AddHours(-1), "Req for u1", [new ReturnRequestUnitDto(u1.Id, [])]), "op-req-u1");
        Assert.True(req.IsSuccess);

        // Setup failing inventory producer
        var failingInvProducer = new Mock<IInventoryCommandProducer>();
        failingInvProducer
            .Setup(p => p.EnqueueApplyReturnedUnitsAsync(It.IsAny<ApplyReturnedUnitsCommandPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.ServiceUnavailable("INVENTORY_QUEUE_DOWN", "Outbox error"));

        await using var db2 = NewDb();
        var service2 = Service(db2, inventoryCommands: failingInvProducer.Object);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);

        var returnReq = new RecordReturnRequest(Now.AddMinutes(-5), "Failing return",
            [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "OK", [])]);
        var returnRes = await service2.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-fail");

        Assert.False(returnRes.IsSuccess);
        Assert.Equal("INVENTORY_QUEUE_DOWN", returnRes.Error.Code);

        // Verify rollback: no ReturnRecords, and ReturnRequestUnit.ClosedAt is STILL NULL!
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Empty(reloadedItem.ReturnRecords);
        Assert.Null(reloadedItem.ReturnRequests.Single().Units.Single().ClosedAt);
    }

    [Fact]
    public async Task SupplierReplay_PreservesClosedAt_DoesNotDuplicateOutboxOrAudit()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        // Create request
        await using var db1 = NewDb();
        var service1 = Service(db1);
        await service1.CreateReturnRequestAsync(item.Id, RentalCallerContext.ForUser(order.RenterId),
            new CreateReturnRequest(Now.AddHours(-1), "Req", [new ReturnRequestUnitDto(u1.Id, [])]), "op-req-rep");

        // Supplier return
        var closeTime = Now.AddMinutes(10);
        _clock.SetUtcNow(closeTime);
        await using var db2 = NewDb();
        var service2 = Service(db2);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);
        var returnReq = new RecordReturnRequest(Now.AddMinutes(5), "Return",
            [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "OK", [])]);

        var firstResp = await service2.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-rep");
        Assert.True(firstResp.IsSuccess);

        await using var checkDb1 = NewDb();
        var initialOutboxCount = await checkDb1.RentalOutboxMessages.CountAsync();
        var initialTransitions = await checkDb1.RentalStateTransitions.CountAsync();

        // Advance time and replay with same key
        _clock.SetUtcNow(Now.AddHours(2));
        await using var db3 = NewDb();
        var service3 = Service(db3);
        var replayResp = await service3.RecordReturnAsync(item.Id, supplierCaller, returnReq, "op-ret-rep");

        Assert.True(replayResp.IsSuccess);
        Assert.Equal(firstResp.Value.ReturnRecordId, replayResp.Value.ReturnRecordId);

        // Verify ClosedAt timestamp did NOT change, and no duplicate records
        await using var checkDb2 = NewDb();
        var reloadedItem = await checkDb2.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(closeTime, reloadedItem.ReturnRequests.Single().Units.Single().ClosedAt);
        Assert.Equal(initialOutboxCount, await checkDb2.RentalOutboxMessages.CountAsync());
        Assert.Equal(initialTransitions, await checkDb2.RentalStateTransitions.CountAsync());
    }

    [Fact]
    public async Task Concurrency_RenterRequestAndSupplierReturn_SerializesCleanly()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);

        var barrier = new SemaphoreSlim(0, 2);

        var renterTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = Service(db);
            var caller = RentalCallerContext.ForUser(order.RenterId);
            return await service.CreateReturnRequestAsync(item.Id, caller,
                new CreateReturnRequest(Now.AddHours(-1), "Concurrent request", [new ReturnRequestUnitDto(u1.Id, [])]),
                "op-conc-renter");
        });

        var supplierTask = Task.Run(async () =>
        {
            await barrier.WaitAsync();
            await using var db = NewDb();
            var service = Service(db);
            var caller = RentalCallerContext.ForUser(item.SupplierId);
            return await service.RecordReturnAsync(item.Id, caller,
                new RecordReturnRequest(Now.AddHours(-1), "Concurrent return",
                    [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "OK", [])]),
                "op-conc-supplier");
        });

        barrier.Release(2);
        await Task.WhenAll(renterTask, supplierTask);

        var renterResult = await renterTask;
        var supplierResult = await supplierTask;

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        // Exactly one of two valid sequences occurred:
        // 1. Renter ran first: both succeeded, and request unit was closed by supplier return.
        // 2. Supplier ran first: supplier succeeded, renter failed with AssetAlreadyReturned.
        if (renterResult.IsSuccess)
        {
            Assert.True(supplierResult.IsSuccess);
            Assert.Single(reloadedItem.ReturnRequests);
            Assert.Single(reloadedItem.ReturnRecords);
            Assert.NotNull(reloadedItem.ReturnRequests.Single().Units.Single().ClosedAt);
        }
        else
        {
            Assert.True(supplierResult.IsSuccess);
            Assert.Equal(RentalErrors.AssetAlreadyReturned(u1.Id).Code, renterResult.Error.Code);
            Assert.Empty(reloadedItem.ReturnRequests);
            Assert.Single(reloadedItem.ReturnRecords);
        }
    }

    [Fact]
    public async Task E2E_Api_FullPartialReturnFlow_UsesHttpResponseIds()
    {
        var (order, item) = await SeedOrderAsync(2);

        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _connection);
            builder.UseSetting("InternalAuthentication:ApiKey", "dev-internal-api-key-dprental-2026");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<RentalServiceDbContext>();
                services.AddScoped(_ => NewDb());
                services.RemoveAll<IOrderLockManager>();
                services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance));
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
                services.AddSingleton<TimeProvider>(_clock);
                services.AddAuthentication("TestScheme")
                    .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("TestScheme", _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = "TestScheme";
                    options.DefaultAuthenticateScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                });
            });
        });

        using var renterClient = factory.CreateClient();
        renterClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        renterClient.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        renterClient.DefaultRequestHeaders.Add(OrderApiHeaders.Subject, order.RenterId.ToString());

        using var supplierClient = factory.CreateClient();
        supplierClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("TestScheme");
        supplierClient.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        supplierClient.DefaultRequestHeaders.Add(OrderApiHeaders.Subject, item.SupplierId.ToString());
        // Step 1: Renter GET order detail
        using var getOrderResp = await renterClient.GetAsync($"/api/v1/rental/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.OK, getOrderResp.StatusCode);
        var orderDoc = await getOrderResp.Content.ReadFromJsonAsync<JsonElement>();

        // Step 2: Pick unit with CanRequestReturn == true
        var orderItems = orderDoc.GetProperty("subOrders")[0].GetProperty("items");
        var targetItem = orderItems[0];
        var unitsArray = targetItem.GetProperty("units").EnumerateArray().ToList();
        var eligibleUnit = unitsArray.First(u => u.GetProperty("canRequestReturn").GetBoolean());
        var chosenAssignmentId = eligibleUnit.GetProperty("assignmentId").GetGuid();

        // Step 3: POST return-requests
        var createRequestPayload = new CreateReturnRequest(Now.AddHours(-1), "E2E return request",
            [new ReturnRequestUnitDto(chosenAssignmentId, [])]);
        renterClient.DefaultRequestHeaders.Add("Idempotency-Key", "e2e-return-req-key");
        using var postReqResp = await renterClient.PostAsJsonAsync(
            $"/api/v1/rental/items/{item.Id}/return-requests", createRequestPayload);
        Assert.Equal(HttpStatusCode.OK, postReqResp.StatusCode);

        // Step 4: Supplier GET suborder detail, sees the return request
        var subOrderId = order.SubOrders.First().Id;
        using var getSubOrderResp1 = await supplierClient.GetAsync($"/api/v1/rental/supplier/sub-orders/{subOrderId}");
        Assert.Equal(HttpStatusCode.OK, getSubOrderResp1.StatusCode);
        var subOrderDoc1 = await getSubOrderResp1.Content.ReadFromJsonAsync<JsonElement>();
        var supplierItem1 = subOrderDoc1.GetProperty("items")[0];
        var supplierReturnReqs1 = supplierItem1.GetProperty("returnRequests").EnumerateArray().ToList();
        Assert.Single(supplierReturnReqs1);
        Assert.Equal("E2E return request", supplierReturnReqs1[0].GetProperty("note").GetString());

        // Step 5: Supplier POST returns for that unit
        var recordReturnPayload = new RecordReturnRequest(Now.AddMinutes(-10), "Supplier confirmed return",
            [new ReturnUnitRequest(chosenAssignmentId, UnitCondition.Normal, "Good condition", [])]);
        supplierClient.DefaultRequestHeaders.Add("Idempotency-Key", "e2e-record-return-key");
        using var postReturnResp = await supplierClient.PostAsJsonAsync(
            $"/api/v1/rental/items/{item.Id}/returns", recordReturnPayload);
        Assert.Equal(HttpStatusCode.OK, postReturnResp.StatusCode);

        // Step 6: Renter and Supplier GET again: returned unit in returnRequests has ClosedAt != null
        using var getOrderResp2 = await renterClient.GetAsync($"/api/v1/rental/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.OK, getOrderResp2.StatusCode);
        var orderDoc2 = await getOrderResp2.Content.ReadFromJsonAsync<JsonElement>();
        var reqUnitsRenter = orderDoc2.GetProperty("subOrders")[0].GetProperty("items")[0]
            .GetProperty("returnRequests")[0].GetProperty("units").EnumerateArray().ToList();
        Assert.Single(reqUnitsRenter);
        Assert.True(reqUnitsRenter[0].GetProperty("closedAt").ValueKind != JsonValueKind.Null);

        using var getSubOrderResp2 = await supplierClient.GetAsync($"/api/v1/rental/supplier/sub-orders/{subOrderId}");
        Assert.Equal(HttpStatusCode.OK, getSubOrderResp2.StatusCode);
        var subOrderDoc2 = await getSubOrderResp2.Content.ReadFromJsonAsync<JsonElement>();
        var reqUnitsSupplier = subOrderDoc2.GetProperty("items")[0]
            .GetProperty("returnRequests")[0].GetProperty("units").EnumerateArray().ToList();
        Assert.Single(reqUnitsSupplier);
        Assert.True(reqUnitsSupplier[0].GetProperty("closedAt").ValueKind != JsonValueKind.Null);
    }
}
