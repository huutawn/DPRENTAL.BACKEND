using System.Net;
using System.Net.Http.Json;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Hubs;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

[Collection("PostgreSql")]
public sealed class AdminReturnRequestResolutionTests : IAsyncLifetime
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

    private ReturnService CreateService(
        RentalServiceDbContext db,
        IRentalMediaClient? media = null,
        IInventoryCommandProducer? inventoryProducer = null,
        IRentalEventProducer? eventProducer = null,
        IOrderHubNotifier? notifier = null)
    {
        var outbox = new OutboxRepository(db);
        var settings = Configuration();
        var invCommands = inventoryProducer ?? new InventoryCommandProducer(
            outbox, Options.Create(new KafkaOptions { Enabled = true }), _clock);
        var events = eventProducer ?? new RentalEventProducer(
            new KafkaOutboxWriter(outbox),
            Options.Create(new KafkaOptions { Enabled = true, RentalEventsTopic = KafkaContractSpec.Topics.RentalEvents }),
            _clock);

        return new ReturnService(
            new RentalOrderRepository(db),
            new PostgresOrderLockManager(settings, NullLogger<PostgresOrderLockManager>.Instance),
            db,
            media ?? ReturnRequestMediaStub.ForRenter(db),
            _clock,
            events,
            settings,
            invCommands,
            notifier,
            NullLogger<ReturnService>.Instance);
    }

    private static RentalCallerContext CreateAdminCaller(Guid? adminId = null) =>
        RentalCallerContext.ForUser(adminId ?? Guid.NewGuid(), true, [RentalAuthClaims.StandardRoles.Admin]);
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
    public async Task AdminConfirmReceived_ValidWorkflow_Success()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        Guid adminId = Guid.NewGuid();
        var adminCaller = CreateAdminCaller(adminId);
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        var resolveReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Admin confirmed unit received in good shape",
            [targetUnit.RentalUnitAssignmentId],
            ResolutionMediaIds: null,
            ActualReturnAt: Now.AddHours(-1),
            Units:
            [
                new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Normal, null, [])
            ]);

        var result = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, resolveReq, "resolve-confirm-1");
        Assert.True(result.IsSuccess);

        // Verify return request response
        var confirmedUnitResp = result.Value.Units.Single(u => u.RentalUnitAssignmentId == targetUnit.RentalUnitAssignmentId);
        Assert.Equal(ReturnRequestUnitStatus.Confirmed, confirmedUnitResp.Status);
        Assert.NotNull(confirmedUnitResp.ClosedAt);

        // Verify database: ReturnRecord created with submittedById == adminId
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.UnitAssignments)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Resolutions)
            .SingleAsync(i => i.Id == item.Id);

        var returnRecord = Assert.Single(reloadedItem.ReturnRecords);
        Assert.Equal(adminId, returnRecord.SubmittedById); // Preserves admin actor context!

        var assignment = reloadedItem.UnitAssignments.Single(a => a.Id == targetUnit.RentalUnitAssignmentId);
        Assert.Equal(returnRecord.Id, assignment.ReturnRecordId);
        Assert.Equal(RentalUnitAssignmentStatus.ReturnedNormal, assignment.Status);

        var resolution = Assert.Single(reloadedItem.ReturnRequests.Single().Resolutions);
        Assert.Equal(ReturnResolutionDecision.ConfirmReceived, resolution.Decision);
        Assert.Equal(adminId, resolution.AdminId);
        Assert.Equal("Admin confirmed unit received in good shape", resolution.DecisionNote);

        // Outbox contains inventory command and rental event
        var outbox = await verifyDb.RentalOutboxMessages.ToListAsync();
        Assert.Contains(outbox, m => m.MessageType == "ApplyReturnedUnits");
        Assert.Contains(outbox, m => m.MessageType == "RentalUnitsReturned");
    }

    [Fact]
    public async Task AdminConfirmReceived_DamagedWithoutDescriptionOrMedia_FailsValidation()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var adminCaller = CreateAdminCaller();
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        // Damaged without description
        var reqNoDesc = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Decision note",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Damaged, null, [Guid.NewGuid()])]);

        var resNoDesc = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, reqNoDesc, "damaged-no-desc");
        Assert.True(resNoDesc.IsFailure);
        Assert.Equal(ErrorType.Validation, resNoDesc.Error.Type);

        // Damaged without media
        var reqNoMedia = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Decision note",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Damaged, "Lens cracked", [])]);

        var resNoMedia = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, reqNoMedia, "damaged-no-media");
        Assert.True(resNoMedia.IsFailure);
        Assert.Equal(ErrorType.Validation, resNoMedia.Error.Type);
    }

    [Fact]
    public async Task AdminRejectRequest_NoSideEffects_AllowsRenterNewRequest()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        Guid adminId = Guid.NewGuid();
        var adminCaller = CreateAdminCaller(adminId);
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        var rejectReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.RejectRequest,
            "Invalid claim by renter - machine was not delivered to store",
            [targetUnit.RentalUnitAssignmentId]);

        var result = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, rejectReq, "reject-op-1");
        Assert.True(result.IsSuccess);

        var rejectedUnitResp = result.Value.Units.Single(u => u.RentalUnitAssignmentId == targetUnit.RentalUnitAssignmentId);
        Assert.Equal(ReturnRequestUnitStatus.Rejected, rejectedUnitResp.Status);
        Assert.NotNull(rejectedUnitResp.RejectedAt);
        Assert.Null(rejectedUnitResp.ClosedAt);

        // Verify database: NO ReturnRecord created, assignment ReturnRecordId is STILL NULL, NO inventory outbox commands
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.UnitAssignments)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Resolutions)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Empty(reloadedItem.ReturnRecords);
        var assignment = reloadedItem.UnitAssignments.Single(a => a.Id == targetUnit.RentalUnitAssignmentId);
        Assert.Null(assignment.ReturnRecordId);
        Assert.Equal(RentalUnitAssignmentStatus.HandedOver, assignment.Status);

        var outbox = await verifyDb.RentalOutboxMessages.ToListAsync();
        Assert.DoesNotContain(outbox, m => m.MessageType == "ApplyReturnedUnits");

        // CRITICAL CHECK: After rejection, unit does NOT block creating a new return request!
        var renterCaller = RentalCallerContext.ForUser(order.RenterId);
        var newReturnReq = new CreateReturnRequest(Now.AddMinutes(-5), "Second attempt to return",
            [new ReturnRequestUnitDto(targetUnit.RentalUnitAssignmentId, [Guid.NewGuid()])]);

        var newReqResult = await service.CreateReturnRequestAsync(item.Id, renterCaller, newReturnReq, "renter-new-req-after-reject");
        Assert.True(newReqResult.IsSuccess);
    }

    [Fact]
    public async Task AdminResolve_PartialMixedOutcomes_TwoOperations_ResolvesEntireRequest()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var adminCaller = CreateAdminCaller();
        var unit1 = returnReq.Units.First();
        var unit2 = returnReq.Units.Last();

        await using var db = NewDb();
        var service = CreateService(db);

        // Op 1: Confirm unit 1
        var op1 = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Unit 1 is fine",
            [unit1.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(unit1.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

        var res1 = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, op1, "op-mix-1");
        Assert.True(res1.IsSuccess);
        Assert.Equal(ReturnRequestStatus.PendingSupplierConfirmation, res1.Value.Status); // unit 2 is still actionable

        // Op 2: Reject unit 2
        var op2 = new AdminResolveReturnRequest(
            ReturnResolutionDecision.RejectRequest,
            "Unit 2 rejected",
            [unit2.RentalUnitAssignmentId]);

        var res2 = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, op2, "op-mix-2");
        Assert.True(res2.IsSuccess);

        // Entire request has mixed outcome (Confirmed + Rejected) -> Status = Resolved!
        Assert.Equal(ReturnRequestStatus.Resolved, res2.Value.Status);
    }

    [Fact]
    public async Task AdminResolve_RollbackOnFailure_LeavesNoDanglingDecisionOrRecord()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var adminCaller = CreateAdminCaller();
        var unit1 = returnReq.Units.First();

        var failingEventProducer = new Mock<IRentalEventProducer>();
        failingEventProducer.Setup(p => p.EnqueueRentalUnitsReturnedAsync(
            It.IsAny<RentalUnitsReturnedPayload>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Error.Failure("KAFKA_DOWN", "Failed to enqueue outbox event."));

        await using var db = NewDb();
        var service = CreateService(db, eventProducer: failingEventProducer.Object);

        var resolveReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Will fail due to kafka",
            [unit1.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(unit1.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

        var result = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, resolveReq, "fail-rollback-key");
        Assert.True(result.IsFailure);

        // Verify complete rollback in DB
        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRecords)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Resolutions)
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Empty(reloadedItem.ReturnRecords);
        Assert.Empty(reloadedItem.ReturnRequests.Single().Resolutions);
        Assert.All(reloadedItem.ReturnRequests.Single().Units, u => Assert.Null(u.ClosedAt));
    }

    [Fact]
    public async Task SupplierDirectReturn_ClosesActionableDisputedUnits_PreservesReportHistory()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var unit1 = returnReq.Units.First();

        // 1. Report issue on unit1
        await using (var dbIssue = NewDb())
        {
            var r = await dbIssue.ReturnRequests.Include(r => r.Issues).SingleAsync(r => r.Id == returnReq.Id);
            r.AddIssue(new ReturnRequestIssue(
                Guid.NewGuid(), returnReq.Id, order.RenterId, ActorRole.Renter,
                "Supplier delayed confirmation", null, null, Now.AddHours(-1), [unit1.RentalUnitAssignmentId]));
            await dbIssue.SaveChangesAsync();
        }

        // 2. Supplier directly confirms return using POST /returns
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);
        await using var db = NewDb();
        var service = CreateService(db);

        var returnPayload = new RecordReturnRequest(
            Now,
            "Supplier physically received unit",
            [new ReturnUnitRequest(unit1.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

        var supplierResult = await service.RecordReturnAsync(item.Id, supplierCaller, returnPayload, "supplier-confirm-key");
        Assert.True(supplierResult.IsSuccess);

        // Verify unit is closed, and issue history is preserved!
        await using var verifyDb = NewDb();
        var reloadedReq = await verifyDb.ReturnRequests
            .Include(r => r.Units)
            .Include(r => r.Issues)
            .SingleAsync(r => r.Id == returnReq.Id);

        var u1 = reloadedReq.Units.Single(u => u.RentalUnitAssignmentId == unit1.RentalUnitAssignmentId);
        Assert.NotNull(u1.ClosedAt);
        Assert.Single(reloadedReq.Issues); // History preserved!
    }

    [Fact]
    public async Task SupplierConfirm_AfterRequestRejected_DoesNotAlterRejectedStatus()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var unit = returnReq.Units.Single();

        // 1. Admin rejects request unit
        var adminCaller = CreateAdminCaller();
        await using (var db = NewDb())
        {
            var service = CreateService(db);
            var rejectReq = new AdminResolveReturnRequest(
                ReturnResolutionDecision.RejectRequest, "Rejected request", [unit.RentalUnitAssignmentId]);
            var rejectRes = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, rejectReq, "reject-key");
            Assert.True(rejectRes.IsSuccess);
        }

        // 2. Later, Supplier receives the machine and calls POST /returns
        var supplierCaller = RentalCallerContext.ForUser(item.SupplierId);
        await using (var db2 = NewDb())
        {
            var service2 = CreateService(db2);
            var returnPayload = new RecordReturnRequest(
                Now,
                "Later received by supplier",
                [new ReturnUnitRequest(unit.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

            var supplierRes = await service2.RecordReturnAsync(item.Id, supplierCaller, returnPayload, "supplier-later-confirm");
            Assert.True(supplierRes.IsSuccess);
        }

        // Verify: the old return request unit is STILL Rejected (RejectedAt != null, ClosedAt == null)
        await using var verifyDb = NewDb();
        var u = await verifyDb.ReturnRequestUnits.SingleAsync(u => u.RentalUnitAssignmentId == unit.RentalUnitAssignmentId);
        Assert.NotNull(u.RejectedAt);
        Assert.Null(u.ClosedAt); // Not overwritten!
    }

    [Fact]
    public async Task AdminReturnRequestQuery_ListAndDetail_Endpoints()
    {
        using OrderApiFactory factory = new();
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalServiceDbContext>();
            db.RentalOrders.Add(order);
            await db.SaveChangesAsync();
        }

        // 1. Non-admin caller receives 403 Forbidden
        using var renterClient = factory.CreateClientFor(order.RenterId);
        using var forbiddenListResp = await renterClient.GetAsync("/api/v1/rental/admin/return-requests");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenListResp.StatusCode);

        // 2. Admin caller can query list
        using var adminClient = factory.CreateClientFor(Guid.NewGuid(), roles: "admin");
        using var listResp = await adminClient.GetAsync("/api/v1/rental/admin/return-requests");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseUpper) }
        };
        var listBody = await listResp.Content.ReadFromJsonAsync<PagedResponse<AdminReturnRequestSummaryResponse>>(jsonOptions);
        Assert.NotNull(listBody);
        Assert.Single(listBody.Items);
        Assert.Equal(returnReq.Id, listBody.Items[0].ReturnRequestId);

        // 3. Admin caller can query detail
        using var detailResp = await adminClient.GetAsync($"/api/v1/rental/admin/return-requests/{returnReq.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);

        var detailBody = await detailResp.Content.ReadFromJsonAsync<AdminReturnRequestDetailResponse>(jsonOptions);
        Assert.NotNull(detailBody);
        Assert.Equal(returnReq.Id, detailBody.Request.ReturnRequestId);
        Assert.Equal(order.Id, detailBody.OrderId);
        Assert.Equal(2, detailBody.InventoryUnits.Count);
    }

    [Fact]
    public async Task AdminResolveReturnRequest_InvalidDecision_FailsValidation()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var adminCaller = CreateAdminCaller();
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        var invalidDecisionReq = new AdminResolveReturnRequest(
            (ReturnResolutionDecision)99,
            "Invalid decision value",
            [targetUnit.RentalUnitAssignmentId]);

        var result = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, invalidDecisionReq, "invalid-decision-1");
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("ReturnResolution.InvalidDecision", result.Error.Code);

        var zeroDecisionReq = new AdminResolveReturnRequest(
            (ReturnResolutionDecision)0,
            "Zero decision value",
            [targetUnit.RentalUnitAssignmentId]);

        var resultZero = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, zeroDecisionReq, "invalid-decision-2");
        Assert.True(resultZero.IsFailure);
        Assert.Equal(ErrorType.Validation, resultZero.Error.Type);
        Assert.Equal("ReturnResolution.InvalidDecision", resultZero.Error.Code);
    }

    [Fact]
    public async Task AdminConfirmReceived_MissingOrEmptyActualReturnAt_FailsValidation()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var adminCaller = CreateAdminCaller();
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        var nullTimeReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Confirmed without return time",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: null,
            Units: [new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

        var resultNull = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, nullTimeReq, "missing-time-1");
        Assert.True(resultNull.IsFailure);
        Assert.Equal("ReturnResolution.InvalidActualReturnAt", resultNull.Error.Code);

        var defaultTimeReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Confirmed with default return time",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: default(DateTimeOffset),
            Units: [new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Normal, null, [])]);

        var resultDefault = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, defaultTimeReq, "missing-time-2");
        Assert.True(resultDefault.IsFailure);
        Assert.Equal("ReturnResolution.InvalidActualReturnAt", resultDefault.Error.Code);
    }

    [Fact]
    public async Task AdminConfirmReceived_NullOrEmptyUnits_OrUnitsContainingNullOrNullMedia_FailsValidation()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(1);
        var adminCaller = CreateAdminCaller();
        var targetUnit = returnReq.Units.First();

        await using var db = NewDb();
        var service = CreateService(db);

        // Null units
        var nullUnitsReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Confirmed with null units",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: null);

        var resNull = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, nullUnitsReq, "null-units");
        Assert.True(resNull.IsFailure);
        Assert.Equal("ReturnResolution.InvalidUnits", resNull.Error.Code);

        // Empty units
        var emptyUnitsReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Confirmed with empty units",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: []);

        var resEmpty = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, emptyUnitsReq, "empty-units");
        Assert.True(resEmpty.IsFailure);
        Assert.Equal("ReturnResolution.InvalidUnits", resEmpty.Error.Code);

        // Unit with null MediaIds
        var nullMediaReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Confirmed with null media list in unit",
            [targetUnit.RentalUnitAssignmentId],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(targetUnit.RentalUnitAssignmentId, UnitCondition.Normal, null, null!)]);

        var resNullMedia = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, nullMediaReq, "null-media-list");
        Assert.True(resNullMedia.IsFailure);
        Assert.Equal("ReturnResolution.InvalidUnits", resNullMedia.Error.Code);
    }

    [Fact]
    public async Task AdminConfirmReceived_UnitsMismatchWithAssignmentIds_FailsValidation()
    {
        var (order, item, returnReq) = await SeedOrderWithReturnRequestAsync(2);
        var adminCaller = CreateAdminCaller();
        var u1 = returnReq.Units.First().RentalUnitAssignmentId;
        var u2 = returnReq.Units.Last().RentalUnitAssignmentId;

        await using var db = NewDb();
        var service = CreateService(db);

        // AssignmentIds specifies u1, but Units specifies u2
        var mismatchReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Mismatched unit and assignment",
            [u1],
            ActualReturnAt: Now.AddHours(-1),
            Units: [new ReturnUnitRequest(u2, UnitCondition.Normal, null, [])]);

        var resMismatch = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, mismatchReq, "mismatch-1");
        Assert.True(resMismatch.IsFailure);
        Assert.Equal("ReturnResolution.UnitsMismatch", resMismatch.Error.Code);

        // Duplicate assignment in Units
        var dupUnitReq = new AdminResolveReturnRequest(
            ReturnResolutionDecision.ConfirmReceived,
            "Duplicate unit in units",
            [u1],
            ActualReturnAt: Now.AddHours(-1),
            Units: [
                new ReturnUnitRequest(u1, UnitCondition.Normal, null, []),
                new ReturnUnitRequest(u1, UnitCondition.Normal, null, [])
            ]);

        var resDup = await service.ResolveReturnRequestAsync(item.Id, returnReq.Id, adminCaller, dupUnitReq, "mismatch-dup");
        Assert.True(resDup.IsFailure);
        Assert.Equal("ReturnResolution.UnitsMismatch", resDup.Error.Code);
    }
}
