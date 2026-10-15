using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Configuration;
using System.Security.Claims;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Returns;

using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Dprental.RentalService.Domain.IRepositories;

using Dprental.RentalService.Api.Tests.Cart;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class ReturnTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
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

    internal static RentalOrder NewOrder(int quantity = 2)
    {
        var order = PostgresTestDatabaseHelper.CreateOrderWithItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), quantity, Now.AddDays(-7));
        var item = order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.InRental);
        var handover = new HandoverRecord(Guid.NewGuid(), item.Id, Now.AddDays(-2), null, item.SupplierId, Now.AddDays(-2), Now.AddDays(-2));
        item.SetHandoverRecord(handover);
        foreach (var unit in item.UnitAssignments)
        {
            unit.RecordHandover(handover.Id, UnitCondition.Normal, null, []);
            handover.AddAssignment(unit);
        }
        return order;
    }

    private static RentalCallerContext Caller(Guid id) => RentalCallerContext.FromClaimsPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id.ToString())], "test")));

    private ReturnService Service(RentalServiceDbContext db, IRentalMediaClient? media = null, IConfiguration? configuration = null)
    {
        var outbox = new OutboxRepository(db);
        // Producer thật ghi lệnh ApplyReturnedUnits vào Outbox cùng transaction với biên bản trả máy.
        var inventoryCommands = new InventoryCommandProducer(
            outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), _clock);
        return new(
            new RentalOrderRepository(db), new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
            db, media ?? Mock.Of<IRentalMediaClient>(), _clock, outbox, configuration ?? Configuration(), inventoryCommands);
    }
    private async Task<RentalItem> SeedAsync(int quantity = 2)
    {
        var order = NewOrder(quantity);
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return order.Items.Single();
    }

    private static RecordReturnRequest Request(Guid assignmentId, UnitCondition condition = UnitCondition.Normal) =>
        new(Now.AddHours(-1), null, [new(assignmentId, condition, "Observed at return", [])]);

    [Fact]
    public async Task Api_EnforcesAuthenticationOwnershipValidationAndReplay()
    {
        var item = await SeedAsync();
        using var parent = new RentalApiFixture();
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<RentalServiceDbContext>();
            services.AddScoped(_ => NewDb());
            services.RemoveAll<IOrderLockManager>();
            services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance));
            services.AddSingleton<TimeProvider>(_clock);
        }));
        using var client = factory.CreateClient();
        string route = $"/api/v1/rental/items/{item.Id}/returns";
        var request = Request(item.UnitAssignments.First().Id);
        using var anonymous = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", "api-key");
        using var foreign = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Renter-Id");
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", item.SupplierId.ToString());
        using var result = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var original = await result.Content.ReadAsStringAsync();
        using var replay = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        using var conflict = await client.PostAsJsonAsync(route, request with { Note = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var missingKey = await client.PostAsJsonAsync(route, request);
        Assert.False(missingKey.IsSuccessStatusCode);
        await using var db = NewDb();
        Assert.Single(await db.ReturnRecords.ToListAsync());
    }

    [Fact]
    public async Task PartialThenFinalDamage_PersistsDeadlineAndReplayDoesNotExtendIt()
    {
        var item = await SeedAsync();
        var units = item.UnitAssignments.ToArray();
        await using (var db = NewDb())
        {
            var first = await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), Request(units[0].Id, UnitCondition.Damaged), "first");
            Assert.True(first.IsSuccess, first.Error.Message);
            Assert.Equal(1, first.Value.RemainingUnits);
            Assert.Null(first.Value.DeductionDeadline);
            Assert.Equal(RentalItemStatus.InRental, first.Value.Status);
        }
        _clock.Advance(TimeSpan.FromDays(1));
        RecordReturnResponse saved;
        await using (var db = NewDb())
        {
            var last = await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), Request(units[1].Id), "last");
            Assert.True(last.IsSuccess, last.Error.Message);
            saved = last.Value;
            Assert.Equal(Now.AddDays(3), saved.DeductionDeadline);
            Assert.Equal(RentalItemStatus.PendingSettlement, saved.Status);
        }
        _clock.Advance(TimeSpan.FromDays(1));
        await using (var db = NewDb())
        {
            var replay = await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), Request(units[1].Id), "last");
            Assert.Equal(saved, replay.Value);
            Assert.Equal(2, await db.ReturnRecords.CountAsync());
            // Mỗi lần trả: một lệnh ApplyReturnedUnits cho Inventory và một sự kiện RentalUnitsReturned; replay không ghi thêm.
            Assert.Equal(2, await db.RentalOutboxMessages.CountAsync(m => m.MessageType == KafkaContractSpec.CommandTypes.ApplyReturnedUnits));
            Assert.Equal(2, await db.RentalOutboxMessages.CountAsync(m => m.MessageType == "RentalUnitsReturned"));
            // Inventory nhận tình trạng từng máy qua lệnh: máy hỏng phải được khóa, máy bình thường được nhả.
            var conditions = (await db.RentalOutboxMessages
                    .Where(m => m.MessageType == KafkaContractSpec.CommandTypes.ApplyReturnedUnits)
                    .ToListAsync())
                .SelectMany(m => System.Text.Json.JsonDocument.Parse(m.Payload).RootElement
                    .GetProperty("payload").GetProperty("units").EnumerateArray()
                    .Select(u => u.GetProperty("condition").GetString()))
                .Order()
                .ToList();
            Assert.Equal(["DAMAGED", "NORMAL"], conditions);
            var pending = await new OperationReceiptRepository(db)
                .GetPendingOperationDescriptorsExcludingActionsAsync(_clock.GetUtcNow(), 50,
                    [RentalItem.ApplyReturnedUnitsAction]);
            Assert.DoesNotContain(pending, r => r.Action == RentalItem.ApplyReturnedUnitsAction);
            Assert.All(await db.RentalUnitAssignments.ToListAsync(), u => Assert.Null(u.AllocationClosedAt));
        }
    }

    [Fact]
    public async Task ConcurrentRequestsForSameUnit_OnlyOneCommits()
    {
        var item = await SeedAsync();
        var request = Request(item.UnitAssignments.First().Id);
        async Task<Result<RecordReturnResponse>> Send(string key)
        {
            await using var db = NewDb();
            return await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, key);
        }
        var results = await Task.WhenAll(Send("a"), Send("b"));
        Assert.Single(results, r => r.IsSuccess);
        await using var check = NewDb();
        Assert.Equal(1, await check.ReturnRecords.CountAsync());
        Assert.Equal(1, await check.RentalOutboxMessages.CountAsync(m => m.MessageType == KafkaContractSpec.CommandTypes.ApplyReturnedUnits));
        Assert.Equal(1, await check.RentalOutboxMessages.CountAsync(m => m.MessageType == "RentalUnitsReturned"));
    }

    [Fact]
    public async Task SameKeyDifferentPayloadOrSupplier_IsRejected()
    {
        var item = await SeedAsync();
        var request = Request(item.UnitAssignments.First().Id);
        await using var db = NewDb();
        Assert.True((await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "key")).IsSuccess);
        var conflict = await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), request with { Note = "changed" }, "key");
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);
        var unauthorized = await Service(db).RecordReturnAsync(item.Id, Caller(Guid.NewGuid()), request, "key");
        Assert.Equal(ErrorType.Forbidden, unauthorized.Error.Type);
    }

    [Fact]
    public async Task InvalidBatch_DoesNotPersistAnyReturn()
    {
        var item = await SeedAsync();
        var request = Request(item.UnitAssignments.First().Id);
        request.Units.Add(new(Guid.NewGuid(), UnitCondition.Normal, null, []));
        await using var db = NewDb();
        Assert.True((await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "key")).IsFailure);
        Assert.Empty(await db.ReturnRecords.ToListAsync());
        Assert.Empty(await db.RentalOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task FailedCommit_RollsBackReturnReceiptAndOutbox_ThenRetrySucceeds()
    {
        var item = await SeedAsync();
        var request = Request(item.UnitAssignments.First().Id);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // PostgreSQL rejects this value; the complete SaveChanges transaction must roll back.
            ["Kafka:RentalEventsTopic"] = new string('x', 257)
        }).Build();
        await using (var db = NewDb())
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Service(db, configuration: configuration)
                .RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "rollback"));
        }
        await using (var db = NewDb())
        {
            Assert.Empty(await db.ReturnRecords.ToListAsync());
            Assert.Empty(await db.RentalOutboxMessages.ToListAsync());
            Assert.False(await db.RentalOperationReceipts.AnyAsync(r => r.OperationId == "rollback"));
            Assert.False(await db.RentalOperationReceipts.AnyAsync(r =>
                r.Action == RentalItem.MarkReturnedAction || r.Action == RentalItem.BlockDamagedAction));
            Assert.All(await db.RentalUnitAssignments.ToListAsync(), u => Assert.Null(u.ReturnRecordId));
            Assert.True((await Service(db).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "rollback")).IsSuccess);
        }
    }

    [Fact]
    public async Task MediaFailure_DoesNotPersistAndSameKeyCanRetry()
    {
        var item = await SeedAsync(1);
        var mediaId = Guid.NewGuid();
        var request = Request(item.UnitAssignments.Single().Id);
        request.Units[0].MediaIds.Add(mediaId);
        var media = new Mock<IRentalMediaClient>();
        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<MediaVerifyDto>(Error.ServiceUnavailable("MEDIA_UNAVAILABLE", "Unavailable")));
        await using (var db = NewDb())
        {
            Assert.True((await Service(db, media.Object).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "key")).IsFailure);
            Assert.Empty(await db.ReturnRecords.ToListAsync());
        }
        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, item.SupplierId.ToString(), "Ready")));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready")));
        await using (var db = NewDb())
        {
            var result = await Service(db, media.Object).RecordReturnAsync(item.Id, Caller(item.SupplierId), request, "key");
            Assert.True(result.IsSuccess, result.Error.Message);
            Assert.Equal(RentalItemStatus.PendingSettlement, result.Value.Status);
            Assert.Null(result.Value.DeductionDeadline);
        }
        await using var check = NewDb();
        Assert.Equal(mediaId, (await check.RentalUnitAssignments.SingleAsync()).ReturnMediaIds.Single());
    }
}
