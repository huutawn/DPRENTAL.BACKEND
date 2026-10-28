using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
public sealed class ReturnRequestTests : IAsyncLifetime
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

    private async Task<(RentalOrder order, RentalItem item)> SeedOrderAsync(int quantity = 3)
    {
        var order = ReturnTests.NewOrder(quantity);
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return (order, order.Items.Single());
    }

    [Fact]
    public async Task PartialReturn_OneOfThreeUnits_CreatesSingleRequestAndUnit_ItemRemainsInRental()
    {
        var (order, item) = await SeedOrderAsync(3);
        var unit1 = item.UnitAssignments.ElementAt(0);
        Guid mediaId = Guid.NewGuid();
        var media = CreateMediaMock(mediaId, order.RenterId);

        await using var db = NewDb();
        var service = Service(db, media.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        var request = new CreateReturnRequest(
            Now.AddHours(-1),
            "Returning 1 machine out of 3",
            [new ReturnRequestUnitDto(unit1.Id, [mediaId])]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, "key-part-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEqual(Guid.Empty, result.Value.ReturnRequestId);
        Assert.Equal(item.Id, result.Value.ItemId);
        Assert.Equal(Now.AddHours(-1), result.Value.ActualReturnAt);
        Assert.Equal("Returning 1 machine out of 3", result.Value.Note);
        Assert.Equal(Now, result.Value.SubmittedAt);
        Assert.Single(result.Value.Units);
        Assert.Equal(unit1.Id, result.Value.Units[0].RentalUnitAssignmentId);
        Assert.Equal(mediaId, result.Value.Units[0].MediaIds![0]);

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(RentalItemStatus.InRental, reloadedItem.Status);
        Assert.Single(reloadedItem.ReturnRequests);
        var savedReq = reloadedItem.ReturnRequests.Single();
        Assert.Single(savedReq.Units);
        Assert.Null(savedReq.Units.Single().ClosedAt);
        Assert.Empty(reloadedItem.ReturnRecords);
    }

    [Fact]
    public async Task MultipleUnits_TwoOrThreeUnits_SucceedsAndItemRemainsInRental()
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);
        var u3 = item.UnitAssignments.ElementAt(2);

        await using var db = NewDb();
        var service = Service(db);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        var request = new CreateReturnRequest(
            Now.AddHours(-2),
            "Returning all 3 machines",
            [
                new ReturnRequestUnitDto(u1.Id, []),
                new ReturnRequestUnitDto(u2.Id, []),
                new ReturnRequestUnitDto(u3.Id, [])
            ]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, "key-all-3");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, result.Value.Units.Count);

        await using var verifyDb = NewDb();
        var reloadedItem = await verifyDb.RentalItems
            .Include(i => i.ReturnRequests).ThenInclude(r => r.Units)
            .SingleAsync(i => i.Id == item.Id);

        Assert.Equal(RentalItemStatus.InRental, reloadedItem.Status);
        Assert.Single(reloadedItem.ReturnRequests);
        Assert.Equal(3, reloadedItem.ReturnRequests.Single().Units.Count);
        Assert.All(reloadedItem.ReturnRequests.Single().Units, u => Assert.Null(u.ClosedAt));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("service")]
    [InlineData("supplier")]
    [InlineData("stranger")]
    [InlineData("admin")]
    public async Task Authorization_RejectsUnauthorizedCallers(string callerType)
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        RentalCallerContext caller = callerType switch
        {
            "anonymous" => RentalCallerContext.Anonymous,
            "service" => RentalCallerContext.ForService("internal-service"),
            "supplier" => RentalCallerContext.ForUser(item.SupplierId),
            "stranger" => RentalCallerContext.ForUser(Guid.NewGuid()),
            "admin" => RentalCallerContext.ForUser(Guid.NewGuid(), roles: ["admin"]),
            _ => throw new ArgumentOutOfRangeException(nameof(callerType))
        };

        await using var db = NewDb();
        var service = Service(db);
        var request = new CreateReturnRequest(Now.AddHours(-1), null, [new ReturnRequestUnitDto(u1.Id, [])]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, "auth-key");

        Assert.True(result.IsFailure);
        if (callerType == "anonymous")
            Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        else if (callerType == "service")
            Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        else
            Assert.Equal(RentalErrors.ItemNotFound.Code, result.Error.Code);
    }

    [Theory]
    [InlineData("unknownAssignment")]
    [InlineData("notHandedOver")]
    [InlineData("alreadyReturned")]
    [InlineData("futureTime")]
    [InlineData("beforeHandoverTime")]
    [InlineData("notInRental")]
    public async Task EquipmentAndTimingValidation_EnforcesRules(string scenario)
    {
        var (order, item) = await SeedOrderAsync(2);
        var unit = item.UnitAssignments.First();

        var assignmentId = unit.Id;
        var returnTime = Now.AddHours(-1);

        await using (var mutateDb = NewDb())
        {
            var trackedOrder = await mutateDb.RentalOrders
                .Include(o => o.Items).ThenInclude(i => i.UnitAssignments)
                .Include(o => o.Items).ThenInclude(i => i.HandoverRecord)
                .SingleAsync(o => o.Id == order.Id);
            var trackedItem = trackedOrder.Items.Single();
            var trackedUnit = trackedItem.UnitAssignments.Single(u => u.Id == assignmentId);

            switch (scenario)
            {
                case "unknownAssignment":
                    assignmentId = Guid.NewGuid();
                    break;
                case "notHandedOver":
                    typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.Status))!.SetValue(trackedUnit, RentalUnitAssignmentStatus.Assigned);
                    await mutateDb.SaveChangesAsync();
                    break;
                case "alreadyReturned":
                    var returnRecord = new ReturnRecord(Guid.NewGuid(), trackedItem.Id, Now.AddDays(-1), null, trackedItem.SupplierId, Now.AddDays(-1));
                    trackedItem.AddReturnRecord(returnRecord);
                    mutateDb.Add(returnRecord);
                    trackedUnit.RecordReturn(returnRecord.Id, UnitCondition.Normal, null, [], null, null);
                    await mutateDb.SaveChangesAsync();
                    break;
                case "futureTime":
                    returnTime = Now.AddHours(2);
                    break;
                case "beforeHandoverTime":
                    returnTime = trackedItem.HandoverRecord!.ActualHandoverAt.AddMinutes(-5);
                    break;
                case "notInRental":
                    typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(trackedItem, RentalItemStatus.Completed);
                    await mutateDb.SaveChangesAsync();
                    break;
            }
        }

        await using var db = NewDb();
        var service = Service(db);
        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(returnTime, null, [new ReturnRequestUnitDto(assignmentId, [])]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, $"val-key-{scenario}");

        Assert.True(result.IsFailure);
        switch (scenario)
        {
            case "unknownAssignment":
                Assert.Equal(RentalErrors.AssignmentNotFound.Code, result.Error.Code);
                break;
            case "notHandedOver":
                Assert.Equal(RentalErrors.AssetNotHandedOver(assignmentId).Code, result.Error.Code);
                break;
            case "alreadyReturned":
                Assert.Equal(RentalErrors.AssetAlreadyReturned(assignmentId).Code, result.Error.Code);
                break;
            case "futureTime":
                Assert.Equal(RentalErrors.FutureReturnTimeNotAllowed.Code, result.Error.Code);
                break;
            case "beforeHandoverTime":
                Assert.Equal(RentalErrors.InvalidReturnTimeBeforeHandover(assignmentId).Code, result.Error.Code);
                break;
            case "notInRental":
                Assert.Equal(RentalErrors.InvalidItemStatusForReturn.Code, result.Error.Code);
                break;
        }

        await using var verifyDb = NewDb();
        Assert.Empty(await verifyDb.ReturnRequests.ToListAsync());
    }

    [Fact]
    public async Task OpenRequest_RejectsSecondRequestWith409_AndBatchWithOneFaultyUnitFailsEntirely()
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);

        await using var db = NewDb();
        var service = Service(db);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        // 1. First request for u1 succeeds
        var req1 = new CreateReturnRequest(Now.AddHours(-1), "Req 1", [new ReturnRequestUnitDto(u1.Id, [])]);
        var res1 = await service.CreateReturnRequestAsync(item.Id, caller, req1, "open-key-1");
        Assert.True(res1.IsSuccess);

        // 2. Second request with different key targeting u1 returns 409 RETURN_REQUEST_ALREADY_OPEN
        var req2 = new CreateReturnRequest(Now.AddHours(-1), "Req 2", [new ReturnRequestUnitDto(u1.Id, [])]);
        var res2 = await service.CreateReturnRequestAsync(item.Id, caller, req2, "open-key-2");
        Assert.True(res2.IsFailure);
        Assert.Equal("RETURN_REQUEST_ALREADY_OPEN", res2.Error.Code);
        Assert.Equal(ErrorType.Conflict, res2.Error.Type);

        // 3. Batch with u1 (already open) and u2 (valid) fails entirely without saving u2
        var reqBatch = new CreateReturnRequest(Now.AddHours(-1), "Batch",
            [new ReturnRequestUnitDto(u1.Id, []), new ReturnRequestUnitDto(u2.Id, [])]);
        var resBatch = await service.CreateReturnRequestAsync(item.Id, caller, reqBatch, "open-key-3");
        Assert.True(resBatch.IsFailure);
        Assert.Equal("RETURN_REQUEST_ALREADY_OPEN", resBatch.Error.Code);

        // Verify only 1 return request and 1 unit exist in db (for u1 from step 1)
        await using var verifyDb = NewDb();
        var allRequests = await verifyDb.ReturnRequests.Include(r => r.Units).ToListAsync();
        Assert.Single(allRequests);
        Assert.Single(allRequests[0].Units);
        Assert.Equal(u1.Id, allRequests[0].Units.Single().RentalUnitAssignmentId);
    }


    [Theory]
    [InlineData("note")]
    [InlineData("time")]
    [InlineData("unit")]
    [InlineData("media")]
    public async Task Idempotency_SameKeyDifferentPayload_Returns409Conflict(string mutation)
    {
        var (order, item) = await SeedOrderAsync(3);
        var u1 = item.UnitAssignments.ElementAt(0);
        var u2 = item.UnitAssignments.ElementAt(1);
        Guid m1 = Guid.NewGuid();
        Guid m2 = Guid.NewGuid();
        var media = CreateMediaMock(m1, order.RenterId);

        await using var db = NewDb();
        var service = Service(db, media.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);

        var original = new CreateReturnRequest(Now.AddHours(-2), "Original note", [new ReturnRequestUnitDto(u1.Id, [m1])]);
        var first = await service.CreateReturnRequestAsync(item.Id, caller, original, "conflict-key");
        Assert.True(first.IsSuccess);

        CreateReturnRequest changed = mutation switch
        {
            "note" => original with { Note = "Different note" },
            "time" => original with { ActualReturnAt = Now.AddHours(-3) },
            "unit" => new CreateReturnRequest(original.ActualReturnAt, original.Note, [new ReturnRequestUnitDto(u2.Id, [m1])]),
            "media" => new CreateReturnRequest(original.ActualReturnAt, original.Note, [new ReturnRequestUnitDto(u1.Id, [m2])]),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var second = await service.CreateReturnRequestAsync(item.Id, caller, changed, "conflict-key");
        Assert.True(second.IsFailure);
        Assert.Equal("IDEMPOTENCY_CONFLICT", second.Error.Code);
        Assert.Equal(ErrorType.Conflict, second.Error.Type);
    }


    [Theory]
    [InlineData("tooManyMedia")]
    [InlineData("duplicateMedia")]
    [InlineData("emptyMediaGuid")]
    [InlineData("mediaNotReady")]
    [InlineData("mediaNotPrivate")]
    [InlineData("uploaderMismatch")]
    [InlineData("mediaServiceFails")]
    public async Task MediaValidation_EnforcesEvidenceRules(string scenario)
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        List<Guid> mediaIds = [Guid.NewGuid()];
        var mediaMock = new Mock<IRentalMediaClient>();

        switch (scenario)
        {
            case "tooManyMedia":
                mediaIds = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();
                break;
            case "duplicateMedia":
                var dup = Guid.NewGuid();
                mediaIds = [dup, dup];
                break;
            case "emptyMediaGuid":
                mediaIds = [Guid.Empty];
                break;
            case "mediaNotReady":
                mediaMock.Setup(m => m.VerifyMediaAsync(mediaIds[0], It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaIds[0], order.RenterId.ToString(), "Processing")));
                break;
            case "mediaNotPrivate":
                mediaMock.Setup(m => m.VerifyMediaAsync(mediaIds[0], It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaIds[0], order.RenterId.ToString(), "Ready")));
                mediaMock.Setup(m => m.GetMetadataAsync(mediaIds[0], It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaIds[0], "Public", "Ready", Now)));
                break;
            case "uploaderMismatch":
                mediaMock.Setup(m => m.VerifyMediaAsync(mediaIds[0], It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaIds[0], Guid.NewGuid().ToString(), "Ready")));
                break;
            case "mediaServiceFails":
                mediaMock.Setup(m => m.VerifyMediaAsync(mediaIds[0], It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Failure<MediaVerifyDto>(Error.ServiceUnavailable("MEDIA_UNAVAILABLE", "Down")));
                break;
        }

        await using var db = NewDb();
        var service = Service(db, mediaMock.Object);
        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(Now.AddHours(-1), null, [new ReturnRequestUnitDto(u1.Id, mediaIds)]);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, $"media-key-{scenario}");

        Assert.True(result.IsFailure);
        await using var verifyDb = NewDb();
        Assert.Empty(await verifyDb.ReturnRequests.ToListAsync());
        Assert.Empty(await verifyDb.ReturnRequestUnits.ToListAsync());
        Assert.Empty(await verifyDb.RentalOperationReceipts.Where(r => r.Action == "RequestReturn").ToListAsync());
    }

    [Theory]
    [InlineData("emptyKey")]
    [InlineData("tooLongKey")]
    [InlineData("nullUnits")]
    [InlineData("emptyUnits")]
    [InlineData("duplicateUnitsInRequest")]
    [InlineData("emptyUnitAssignmentGuid")]
    [InlineData("nullMediaList")]
    [InlineData("noteTooLong")]
    public async Task PayloadValidation_RejectsMalformedInputs(string scenario)
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();

        string key = "valid-key";
        string? note = null;
        List<ReturnRequestUnitDto>? units = [new ReturnRequestUnitDto(u1.Id, [])];

        switch (scenario)
        {
            case "emptyKey": key = "   "; break;
            case "tooLongKey": key = new string('x', 65); break;
            case "nullUnits": units = null; break;
            case "emptyUnits": units = []; break;
            case "duplicateUnitsInRequest": units = [new ReturnRequestUnitDto(u1.Id, []), new ReturnRequestUnitDto(u1.Id, [])]; break;
            case "emptyUnitAssignmentGuid": units = [new ReturnRequestUnitDto(Guid.Empty, [])]; break;
            case "nullMediaList": units = [new ReturnRequestUnitDto(u1.Id, null)]; break;
            case "noteTooLong": note = new string('A', 2001); break;
        }

        await using var db = NewDb();
        var service = Service(db);
        var caller = RentalCallerContext.ForUser(order.RenterId);
        var request = new CreateReturnRequest(Now.AddHours(-1), note, units);

        var result = await service.CreateReturnRequestAsync(item.Id, caller, request, key);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }


    [Fact]
    public async Task EvidenceReadAuthorization_AllowsRenterSupplierAdmin_AndRejectsStranger()
    {
        var (order, item) = await SeedOrderAsync(2);
        var u1 = item.UnitAssignments.First();
        Guid mediaId = Guid.NewGuid();

        // Add return request with mediaId
        Guid reqId = Guid.NewGuid();
        var unitReq = new ReturnRequestUnit(Guid.NewGuid(), reqId, u1.Id, [mediaId]);
        var req = new ReturnRequest(reqId, item.Id, Now.AddHours(-1), "Evidence auth", order.RenterId, Now, [unitReq]);
        item.AddReturnRequest(req);

        await using (var db = NewDb())
        {
            db.Add(req);
            await db.SaveChangesAsync();
        }

        // 1. Renter caller
        var renterAuth = RentalAuthorizationPolicy.AuthorizeEvidenceRead(
            RentalCallerContext.ForUser(order.RenterId), order, item, mediaId);
        Assert.True(renterAuth.IsSuccess);

        // 2. Supplier caller
        var supplierAuth = RentalAuthorizationPolicy.AuthorizeEvidenceRead(
            RentalCallerContext.ForUser(item.SupplierId), order, item, mediaId);
        Assert.True(supplierAuth.IsSuccess);

        // 3. Admin caller
        var adminAuth = RentalAuthorizationPolicy.AuthorizeEvidenceRead(
            RentalCallerContext.ForUser(Guid.NewGuid(), roles: ["admin"]), order, item, mediaId);
        Assert.True(adminAuth.IsSuccess);

        // 4. Stranger caller
        var strangerAuth = RentalAuthorizationPolicy.AuthorizeEvidenceRead(
            RentalCallerContext.ForUser(Guid.NewGuid()), order, item, mediaId);
        Assert.True(strangerAuth.IsFailure);
        Assert.Equal(RentalAuthorizationPolicy.ErrorCodes.EvidenceNotFound, strangerAuth.Error.Code);
    }

}
