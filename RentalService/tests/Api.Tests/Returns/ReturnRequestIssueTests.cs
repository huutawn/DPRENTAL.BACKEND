using System.Net;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Returns;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class ReturnRequestIssueTests : IAsyncLifetime
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

    private static Mock<IRentalMediaClient> CreateMediaMock(Guid mediaId, Guid uploaderId,
        string status = "Ready", string visibility = "Private")
    {
        var media = new Mock<IRentalMediaClient>();
        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, uploaderId.ToString(), status)));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, visibility, status, Now)));
        return media;
    }

    private async Task<(RentalOrder order, RentalItem item, ReturnRequest returnReq)> SeedOrderWithReturnRequestAsync(int quantity = 2)
    {
        var order = ReturnTests.NewOrder(quantity);
        var item = order.Items.Single();
        Guid reqId = Guid.NewGuid();
        var units = item.UnitAssignments.Select(u =>
            new ReturnRequestUnit(Guid.NewGuid(), reqId, u.Id, [])).ToList();
        var returnReq = new ReturnRequest(reqId, item.Id, Now.AddHours(-2), "Initial request", order.RenterId, Now.AddHours(-2), units);
        item.AddReturnRequest(returnReq);

        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, item, returnReq);
    }

    [Fact]
    public async Task ReportReturnIssue_Renter_DefaultsToAllActionableUnits_SuccessfullyMarksDisputed()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        await using var db = NewDb();
        var service = Service(db);

        var reportReq = new ReportReturnIssueRequest(
            "Supplier did not confirm machine handover",
            "Waited 5 hours at shop",
            MediaIds: null,
            AssignmentIds: null); // Defaults to all actionable units

        var result = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, reportReq, "issue-key-1");
        Assert.True(result.IsSuccess);
        Assert.Equal(ReturnRequestStatus.Disputed, result.Value.Status);
        Assert.Equal(2, result.Value.Units.Count);
        Assert.All(result.Value.Units, u => Assert.Equal(ReturnRequestUnitStatus.Disputed, u.Status));
        Assert.NotNull(result.Value.Issues);
        Assert.Single(result.Value.Issues);
        Assert.Equal(order.RenterId, result.Value.Issues[0].ReporterId);
        Assert.Equal(ActorRole.Renter, result.Value.Issues[0].ReporterRole);

        // Verify database: no ReturnRecord created, RentalItem still InRental
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Issues).ThenInclude(i => i.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(RentalItemStatus.InRental, reloadedItem.Status);
        Assert.Empty(reloadedItem.ReturnRecords);
        var savedReq = reloadedItem.ReturnRequests.Single(r => r.Id == returnReq.Id);
        Assert.Single(savedReq.Issues);
        Assert.Equal(2, savedReq.Issues.Single().Units.Count);
    }

    [Fact]
    public async Task ReportReturnIssue_Supplier_TargetsSubsetOfUnits_Successfully()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = Service(db);

        var reportReq = new ReportReturnIssueRequest(
            "Machine not received at store",
            "Checked with staff",
            MediaIds: [],
            AssignmentIds: [targetUnit.RentalUnitAssignmentId]);

        var result = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, supplierCaller, reportReq, "supplier-issue-key");
        Assert.True(result.IsSuccess);
        Assert.Equal(ReturnRequestStatus.Disputed, result.Value.Status);

        var unit1Resp = result.Value.Units.Single(u => u.RentalUnitAssignmentId == targetUnit.RentalUnitAssignmentId);
        Assert.Equal(ReturnRequestUnitStatus.Disputed, unit1Resp.Status);

        var otherUnit = returnReq.Units.Last();
        var unit2Resp = result.Value.Units.Single(u => u.RentalUnitAssignmentId == otherUnit.RentalUnitAssignmentId);
        Assert.Equal(ReturnRequestUnitStatus.PendingSupplierConfirmation, unit2Resp.Status);
    }

    [Fact]
    public async Task ReportReturnIssue_BothSidesReportSeparately_WithoutOverwriting()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);

        Guid renterMediaId = Guid.NewGuid();
        Guid supplierMediaId = Guid.NewGuid();

        var mediaMock = new Mock<IRentalMediaClient>();
        mediaMock.Setup(m => m.VerifyMediaAsync(renterMediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(renterMediaId, order.RenterId.ToString(), "Ready")));
        mediaMock.Setup(m => m.GetMetadataAsync(renterMediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(renterMediaId, "Private", "Ready", Now)));
        mediaMock.Setup(m => m.VerifyMediaAsync(supplierMediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(supplierMediaId, item.SupplierId.ToString(), "Ready")));
        mediaMock.Setup(m => m.GetMetadataAsync(supplierMediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(supplierMediaId, "Private", "Ready", Now)));

        await using var db = NewDb();
        var service = Service(db, mediaMock.Object);

        // Renter reports
        var renterReq = new ReportReturnIssueRequest("Renter issue", "Renter note", [renterMediaId], null);
        var renterResult = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, renterReq, "r-key-1");
        Assert.True(renterResult.IsSuccess);

        // Supplier reports
        var supplierReq = new ReportReturnIssueRequest("Supplier issue", "Supplier note", [supplierMediaId], null);
        var supplierResult = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, supplierCaller, supplierReq, "s-key-1");
        Assert.True(supplierResult.IsSuccess);

        Assert.Equal(2, supplierResult.Value.Issues!.Count);
        Assert.Contains(supplierResult.Value.Issues, i => i.ReporterRole == ActorRole.Renter && i.MediaIds.Contains(renterMediaId));
        Assert.Contains(supplierResult.Value.Issues, i => i.ReporterRole == ActorRole.Supplier && i.MediaIds.Contains(supplierMediaId));
    }

    [Fact]
    public async Task ReportReturnIssue_StrangerOrServiceToken_Rejected()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);

        await using var db = NewDb();
        var service = Service(db);
        var reportReq = new ReportReturnIssueRequest("Reason", null, null, null);

        // Stranger user -> 404
        var strangerCaller = RentalCallerContext.ForUser(Guid.NewGuid());
        var strangerResult = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, strangerCaller, reportReq, "key-stranger");
        Assert.True(strangerResult.IsFailure);
        Assert.Equal(ErrorType.NotFound, strangerResult.Error.Type);

        // Service token -> 403
        var serviceTokenCaller = RentalCallerContext.ForService("payment-worker");
        var tokenResult = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, serviceTokenCaller, reportReq, "key-token");
        Assert.Equal(ErrorType.Forbidden, tokenResult.Error.Type);
    }

    [Fact]
    public async Task ReportReturnIssue_ConfirmedOrRejectedUnit_CannotReceiveNewIssue()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var unit1 = returnReq.Units.First();
        var unit2 = returnReq.Units.Last();

        // Close unit1 and reject unit2
        await using (var closeDb = NewDb())
        {
            var u1 = await closeDb.ReturnRequestUnits.SingleAsync(u => u.Id == unit1.Id);
            u1.SetClosedAt(Now);
            var u2 = await closeDb.ReturnRequestUnits.SingleAsync(u => u.Id == unit2.Id);
            u2.SetRejectedAt(Now);
            await closeDb.SaveChangesAsync();
        }

        await using var db = NewDb();
        var service = Service(db);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        // Targeted closed unit fails with Conflict
        var targetClosed = new ReportReturnIssueRequest("Closed unit issue", null, null, [unit1.RentalUnitAssignmentId]);
        var resClosed = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, targetClosed, "key-closed");
        Assert.True(resClosed.IsFailure);
        Assert.Equal(ErrorType.Conflict, resClosed.Error.Type);

        // Targeted rejected unit fails with Conflict
        var targetRejected = new ReportReturnIssueRequest("Rejected unit issue", null, null, [unit2.RentalUnitAssignmentId]);
        var resRejected = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, targetRejected, "key-rejected");
        Assert.True(resRejected.IsFailure);
        Assert.Equal(ErrorType.Conflict, resRejected.Error.Type);

        // Omitted assignmentIds (all actionable) fails with Conflict when none remain
        var allActionable = new ReportReturnIssueRequest("All open issue", null, null, null);
        var resAll = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, allActionable, "key-all");
        Assert.True(resAll.IsFailure);
        Assert.Equal(ErrorType.Conflict, resAll.Error.Type);
    }

    [Fact]
    public async Task ReportReturnIssue_ValidationFailures_FailEntireBatch()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        await using var db = NewDb();
        var service = Service(db);

        // Empty reason
        var res1 = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller,
            new ReportReturnIssueRequest("", null, null, null), "val-1");
        Assert.True(res1.IsFailure);
        Assert.Equal(ErrorType.Validation, res1.Error.Type);

        // Note > 2000 chars
        var res2 = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller,
            new ReportReturnIssueRequest("Reason", new string('x', 2001), null, null), "val-2");
        Assert.True(res2.IsFailure);
        Assert.Equal(ErrorType.Validation, res2.Error.Type);

        // Media > 10
        var tenMedia = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();
        var res3 = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller,
            new ReportReturnIssueRequest("Reason", null, tenMedia, null), "val-3");
        Assert.True(res3.IsFailure);
        Assert.Equal(ErrorType.Validation, res3.Error.Type);

        // Assignment not in request
        var res4 = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller,
            new ReportReturnIssueRequest("Reason", null, null, [Guid.NewGuid()]), "val-4");
        Assert.True(res4.IsFailure);
        Assert.Equal(ErrorType.Validation, res4.Error.Type);
    }

    [Fact]
    public async Task ReportReturnIssue_IdempotencyReplayAndConflict()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);

        await using var db = NewDb();
        var service = Service(db);

        var reportReq = new ReportReturnIssueRequest("Issue reason", "Note", null, null);
        var first = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, reportReq, "idemp-key");
        Assert.True(first.IsSuccess);

        // Replay with exact same payload
        var replay = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller, reportReq, "idemp-key");
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.ReturnRequestId, replay.Value.ReturnRequestId);

        // Conflict: same key, different reason
        var conflict = await service.ReportReturnIssueAsync(item.Id, returnReq.Id, renterCaller,
            reportReq with { Reason = "Different reason" }, "idemp-key");
        Assert.True(conflict.IsFailure);
        Assert.Equal(ErrorType.Conflict, conflict.Error.Type);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);
    }

    [Fact]
    public async Task ReportReturnIssue_HttpApi_Endpoint_ReturnsOkAndValidResponse()
    {
        using OrderApiFactory factory = new();
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        string url = $"/api/v1/rental/items/{item.Id}/return-requests/{returnReq.Id}/report-issue";
        var payload = new ReportReturnIssueRequest("Machine dispute", "Note via API", null, null);

        // Anonymous -> 401
        using var anonClient = factory.CreateClient();
        using var anonResp = await anonClient.PostAsJsonAsync(url, payload);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // Renter authenticated
        using var renterClient = factory.CreateClientFor(order.RenterId);
        renterClient.DefaultRequestHeaders.Add("Idempotency-Key", "api-issue-key");
        using var resp = await renterClient.PostAsJsonAsync(url, payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var returned = await resp.Content.ReadFromJsonAsync<ReturnRequestResponse>();
        Assert.NotNull(returned);
        Assert.Equal(ReturnRequestStatus.Disputed, returned.Status);
        Assert.NotNull(returned.Issues);
        Assert.Single(returned.Issues);
        Assert.Equal("Machine dispute", returned.Issues[0].Reason);
    }
}
