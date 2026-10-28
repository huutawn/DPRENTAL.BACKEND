using System.Net;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Cart;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
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
public sealed class ReturnRequestConcurrencyAndReplayTests : IAsyncLifetime
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
        new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _connection }).Build();

    private ReturnService Service(RentalServiceDbContext db, IRentalMediaClient? media = null, IConfiguration? configuration = null)
    {
        var outbox = new OutboxRepository(db);
        IConfiguration settings = configuration ?? Configuration();
        var inventoryCommands = new InventoryCommandProducer(
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
            inventoryCommands);
    }

    private async Task<(RentalOrder order, RentalItem item)> SeedOrderAsync(int quantity = 3)
    {
        var order = ReturnTests.NewOrder(quantity);
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, order.Items.Single());
    }

    [Fact]
    public async Task Idempotency_SameKeyAndPayloadReplaysResponse_EvenIfItemStatusChangedLater()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        await using var db = NewDb();
        var service = Service(db);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        var request = new CreateReturnRequest(Now.AddHours(-2), "Initial request", [new ReturnRequestUnitDto(u1.Id, [])]);
        var first = await service.CreateReturnRequestAsync(item.Id, caller, request, "idemp-key-1");
        Assert.True(first.IsSuccess);

        // Change Item status in DB (e.g. to Completed or Cancelled)
        await using (var mutateDb = NewDb())
        {
            var trackedItem = await mutateDb.RentalItems.SingleAsync(i => i.Id == item.Id);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(trackedItem, RentalItemStatus.Completed);
            await mutateDb.SaveChangesAsync();
        }

        // Replay with same key and payload: must succeed and return cached response despite item.Status != InRental
        var replay = await service.CreateReturnRequestAsync(item.Id, caller, request, "idemp-key-1");
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.ReturnRequestId, replay.Value.ReturnRequestId);
        Assert.Equal(first.Value.ActualReturnAt, replay.Value.ActualReturnAt);
        Assert.Equal(first.Value.SubmittedAt, replay.Value.SubmittedAt);
        Assert.Equal(first.Value.Note, replay.Value.Note);
    }

    [Fact]
    public async Task Normalization_ReorderedUnitsAndMediaAndDifferentTimezoneOffset_ReplaysSuccessfully()
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);
        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();

        var media = new Mock<IRentalMediaClient>();
        foreach (var m in new[] { m1, m2 })
        {
            media.Setup(c => c.VerifyMediaAsync(m, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(new MediaVerifyDto(m, order.RenterId.ToString(), "Ready")));
            media.Setup(c => c.GetMetadataAsync(m, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result.Success(new MediaMetadataDto(m, "Private", "Ready", Now)));
        }

        await using var db = NewDb();
        var service = Service(db, media.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        // Original: u1 then u2, m1 then m2, UTC instant 08:00:00Z
        var dtUtc = new DateTimeOffset(2026, 10, 28, 6, 0, 0, TimeSpan.Zero);
        var original = new CreateReturnRequest(dtUtc, "Normalize",
            [new ReturnRequestUnitDto(u1.Id, [m1, m2]), new ReturnRequestUnitDto(u2.Id, [])]);

        var first = await service.CreateReturnRequestAsync(item.Id, caller, original, "norm-key");
        Assert.True(first.IsSuccess);

        // Replay: u2 then u1, m2 then m1, +07:00 timezone (13:00:00+07:00 == 06:00:00Z)
        var dtOffset = new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.FromHours(7));
        var reordered = new CreateReturnRequest(dtOffset, "Normalize",
            [new ReturnRequestUnitDto(u2.Id, []), new ReturnRequestUnitDto(u1.Id, [m2, m1])]);

        var replay = await service.CreateReturnRequestAsync(item.Id, caller, reordered, "norm-key");
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.ReturnRequestId, replay.Value.ReturnRequestId);
    }

    [Fact]
    public async Task Concurrency_ConcurrentRequestsWithSameKeyAndPayload_CreatesSingleRequest()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(Now.AddHours(-1), "Concurrent same key", [new ReturnRequestUnitDto(u1.Id, [])]);

        // Launch 2 concurrent calls with same key
        var task1 = Task.Run(async () =>
        {
            await using var db = NewDb();
            return await Service(db).CreateReturnRequestAsync(item.Id, caller, request, "concurrent-same-key");
        });

        var task2 = Task.Run(async () =>
        {
            await using var db = NewDb();
            return await Service(db).CreateReturnRequestAsync(item.Id, caller, request, "concurrent-same-key");
        });

        var results = await Task.WhenAll(task1, task2);
        Assert.True(results[0].IsSuccess);
        Assert.True(results[1].IsSuccess);
        Assert.Equal(results[0].Value.ReturnRequestId, results[1].Value.ReturnRequestId);

        await using var verifyDb = NewDb();
        Assert.Single(await verifyDb.ReturnRequests.ToListAsync());
        Assert.Single(await verifyDb.ReturnRequestUnits.ToListAsync());
    }

    [Fact]
    public async Task Concurrency_ConcurrentRequestsWithDifferentKeys_OnlyOneSucceeds()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        var caller = RentalCallerContext.ForUser(order.RenterId);
        var req1 = new CreateReturnRequest(Now.AddHours(-1), "Req 1", [new ReturnRequestUnitDto(u1.Id, [])]);
        var req2 = new CreateReturnRequest(Now.AddHours(-1), "Req 2", [new ReturnRequestUnitDto(u1.Id, [])]);

        var task1 = Task.Run(async () =>
        {
            await using var db = NewDb();
            return await Service(db).CreateReturnRequestAsync(item.Id, caller, req1, "concurrent-diff-1");
        });

        var task2 = Task.Run(async () =>
        {
            await using var db = NewDb();
            return await Service(db).CreateReturnRequestAsync(item.Id, caller, req2, "concurrent-diff-2");
        });

        var results = await Task.WhenAll(task1, task2);
        int successCount = results.Count(r => r.IsSuccess);
        int conflictCount = results.Count(r => r.IsFailure && r.Error.Code == "RETURN_REQUEST_ALREADY_OPEN");

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        await using var verifyDb = NewDb();
        Assert.Single(await verifyDb.ReturnRequests.ToListAsync());
        Assert.Single(await verifyDb.ReturnRequestUnits.ToListAsync());
    }

    [Fact]
    public async Task CrossTask_SupplierConfirmReturn_PreventsSubsequentReturnRequestForConfirmedUnit()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        // 1. Supplier confirms return of u1 via RecordReturnAsync
        await using (var db = NewDb())
        {
            var service = Service(db);
            var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);
            var confirmReq = new RecordReturnRequest(
                Now.AddHours(-1),
                "Supplier confirmed u1 return",
                [new ReturnUnitRequest(u1.Id, UnitCondition.Normal, "Good", [])]);

            var confirmResult = await service.RecordReturnAsync(item.Id, supplierCaller, confirmReq, "supplier-confirm-key");
            Assert.True(confirmResult.IsSuccess, confirmResult.Error.Message);
        }

        // 2. Renter attempts to create return request for u1 -> rejected with AssetAlreadyReturned
        await using (var db2 = NewDb())
        {
            var service2 = Service(db2);
            var renterCaller = RentalCallerContext.ForUser(order.RenterId);
            var renterReq = new CreateReturnRequest(Now.AddHours(-1), null, [new ReturnRequestUnitDto(u1.Id, [])]);

            var renterResult = await service2.CreateReturnRequestAsync(item.Id, renterCaller, renterReq, "renter-req-confirmed-unit");
            Assert.True(renterResult.IsFailure);
            Assert.Equal(RentalErrors.AssetAlreadyReturned(u1.Id).Code, renterResult.Error.Code);
        }

        // 3. Renter can still request return for unconfirmed unit u2
        await using (var db3 = NewDb())
        {
            var service3 = Service(db3);
            var renterCaller = RentalCallerContext.ForUser(order.RenterId);
            var renterReq2 = new CreateReturnRequest(Now.AddHours(-1), null, [new ReturnRequestUnitDto(u2.Id, [])]);

            var renterResult2 = await service3.CreateReturnRequestAsync(item.Id, renterCaller, renterReq2, "renter-req-u2");
            Assert.True(renterResult2.IsSuccess);
        }

        // 4. Invariant: Return request does not emit Inventory command or alter ReturnRecordId
        await using var verifyDb = NewDb();
        var reloadedOrder = await verifyDb.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
        var outboxCommands = await verifyDb.RentalOutboxMessages
            .Where(m => m.MessageType == KafkaContractSpec.CommandTypes.ApplyReturnedUnits)
            .ToListAsync();
        // Only 1 outbox command from the supplier confirm return in step 1, NONE from return requests
        Assert.Single(outboxCommands);
    }

    [Fact]
    public async Task Api_HttpEndpoint_EnforcesAuthValidationIdempotencyAndConflict()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        using var parent = new RentalApiFixture();
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<RentalServiceDbContext>();
            services.AddScoped(_ => NewDb());
            services.RemoveAll<IOrderLockManager>();
            services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance));
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddSingleton<TimeProvider>(_clock);
        }));

        using var client = factory.CreateClient();
        string route = $"/api/v1/rental/items/{item.Id}/return-requests";
        var requestPayload = new CreateReturnRequest(Now.AddHours(-1), "API test note", [new ReturnRequestUnitDto(u1.Id, [])]);

        // 1. Anonymous request (no api key or auth)
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        using var anonResp = await client.PostAsJsonAsync(route, requestPayload);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Add API key and stranger renter ID -> 404 (IDOR)
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", "api-return-req-key");

        using var strangerResp = await client.PostAsJsonAsync(route, requestPayload);
        Assert.Equal(HttpStatusCode.NotFound, strangerResp.StatusCode);

        // 3. Proper renter ID -> 200 OK
        client.DefaultRequestHeaders.Remove("X-Test-Renter-Id");
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", order.RenterId.ToString());

        using var okResp = await client.PostAsJsonAsync(route, requestPayload);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        var originalContent = await okResp.Content.ReadAsStringAsync();

        // 4. Replay with same key -> 200 OK with same content
        using var replayResp = await client.PostAsJsonAsync(route, requestPayload);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
        Assert.Equal(originalContent, await replayResp.Content.ReadAsStringAsync());

        // 5. Conflict with same key different payload -> 409 Conflict
        using var conflictResp = await client.PostAsJsonAsync(route, requestPayload with { Note = "Different note" });
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);

        // 6. Another request with different key for same unit -> 409 Conflict (RETURN_REQUEST_ALREADY_OPEN)
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "api-return-req-key-2");
        using var alreadyOpenResp = await client.PostAsJsonAsync(route, requestPayload);
        Assert.Equal(HttpStatusCode.Conflict, alreadyOpenResp.StatusCode);
    }
}
