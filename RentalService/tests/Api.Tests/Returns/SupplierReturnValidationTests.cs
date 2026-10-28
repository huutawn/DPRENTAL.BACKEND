using System.Security.Claims;
using System.Text.Json;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class SupplierReturnValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static (ReturnService Service, RentalOrder Order, RentalItem Item, Mock<IRentalMediaClient> Media) CreateService(
        RentalOrder? order = null,
        Mock<IRentalMediaClient>? mediaClient = null)
    {
        order ??= ReturnTests.NewOrder(quantity: 2);
        var item = order.Items.Single();
        var repository = new Mock<IRentalOrderRepository>();
        repository.Setup(r => r.GetOrderIdByItemIdAsync(item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order.Id);
        repository.Setup(r => r.GetByIdWithDetailsAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        repository.Setup(r => r.UpdateAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var locks = new Mock<IOrderLockManager>();
        locks.Setup(l => l.AcquireOrderLockAsync(order.Id, It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoopLock());

        var tx = new Mock<IUnitOfWorkTransaction>();
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(tx.Object);
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var media = mediaClient ?? new Mock<IRentalMediaClient>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:Enabled"] = "true"
        }).Build();

        var producer = new Mock<IInventoryCommandProducer>();
        producer.Setup(p => p.EnqueueApplyReturnedUnitsAsync(It.IsAny<ApplyReturnedUnitsCommandPayload>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProducerEnqueueResult(Guid.NewGuid(), false,
                KafkaContractSpec.CommandTypes.ApplyReturnedUnits, KafkaContractSpec.Topics.InventoryCommands, order.Id.ToString("D")));

        var service = new ReturnService(
            repository.Object,
            locks.Object,
            uow.Object,
            media.Object,
            new FakeTimeProvider(Now),
            new FakeRentalEventProducer(),
            config,
            producer.Object);

        return (service, order, item, media);
    }

    private static RentalCallerContext SupplierCaller(Guid supplierId) =>
        RentalCallerContext.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", supplierId.ToString())], "test")));

    private static RentalCallerContext ServiceCaller() =>
        RentalCallerContext.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("scope", "service")], "test")));

    [Fact]
    public async Task ServiceCaller_OrUnauthorizedCaller_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);

        var serviceResult = await service.RecordReturnAsync(item.Id, ServiceCaller(), request, "op-service");
        Assert.True(serviceResult.IsFailure);

        var otherSupplier = await service.RecordReturnAsync(item.Id, SupplierCaller(Guid.NewGuid()), request, "op-other");
        Assert.Equal(RentalErrors.ItemNotFound.Code, otherSupplier.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingIdempotencyKey_IsRejected(string key)
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, key);
        Assert.Equal("INVALID_IDEMPOTENCY_KEY", result.Error.Code);
    }

    [Fact]
    public async Task IdempotencyKey_Over64Chars_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, new string('k', 65));
        Assert.Equal("INVALID_IDEMPOTENCY_KEY", result.Error.Code);
    }

    [Fact]
    public async Task ItemNotInRental_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.AwaitingHandover);
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-status");
        Assert.Equal(RentalErrors.InvalidItemStatusForReturn.Code, result.Error.Code);
    }

    [Fact]
    public async Task DefaultOrFutureReturnTime_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();

        var defaultReq = new RecordReturnRequest(default, null, [new(unit.Id, UnitCondition.Normal, null, [])]);
        var defaultRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), defaultReq, "op-default");
        Assert.Equal("Return.InvalidRequest", defaultRes.Error.Code);

        var futureReq = new RecordReturnRequest(Now.AddMinutes(5), null, [new(unit.Id, UnitCondition.Normal, null, [])]);
        var futureRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), futureReq, "op-future");
        Assert.Equal(RentalErrors.FutureReturnTimeNotAllowed.Code, futureRes.Error.Code);
    }

    [Fact]
    public async Task ReturnTimeBeforeHandover_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var earlyTime = item.HandoverRecord!.ActualHandoverAt.AddMinutes(-5);

        var request = new RecordReturnRequest(earlyTime, null, [new(unit.Id, UnitCondition.Normal, null, [])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-early");
        Assert.Equal(RentalErrors.InvalidReturnTimeBeforeHandover(unit.Id).Code, result.Error.Code);
    }

    [Fact]
    public async Task NoteOver2000Chars_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), new string('x', 2001), [new(unit.Id, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-note");
        Assert.Equal("Return.InvalidRequest", result.Error.Code);
    }

    [Fact]
    public async Task EmptyOrDuplicateUnits_AreRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();

        var emptyReq = new RecordReturnRequest(Now.AddHours(-1), null, []);
        var emptyRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), emptyReq, "op-empty");
        Assert.Equal("Return.InvalidUnits", emptyRes.Error.Code);

        var dupReq = new RecordReturnRequest(Now.AddHours(-1), null, [
            new(unit.Id, UnitCondition.Normal, null, []),
            new(unit.Id, UnitCondition.Normal, null, [])
        ]);
        var dupRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), dupReq, "op-dup");
        Assert.Equal("Return.InvalidUnits", dupRes.Error.Code);
    }

    [Fact]
    public async Task ForeignAssignment_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var foreignId = Guid.NewGuid();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(foreignId, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-foreign");
        Assert.Equal(RentalErrors.AssignmentNotFound.Code, result.Error.Code);
    }

    [Fact]
    public async Task AlreadyReturnedAssignment_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.ReturnRecordId))!.SetValue(unit, Guid.NewGuid());

        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-already");
        Assert.Equal(RentalErrors.AssetAlreadyReturned(unit.Id).Code, result.Error.Code);
    }

    [Fact]
    public async Task AssignmentNotHandedOver_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        typeof(RentalUnitAssignment).GetProperty(nameof(RentalUnitAssignment.Status))!.SetValue(unit, RentalUnitAssignmentStatus.Assigned);

        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-nothanded");
        Assert.Equal(RentalErrors.AssetNotHandedOver(unit.Id).Code, result.Error.Code);
    }

    [Fact]
    public async Task DamagedUnit_MissingDescription_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var mediaId = Guid.NewGuid();

        var nullDesc = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, null, [mediaId])]);
        var nullRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), nullDesc, "op-nulldesc");
        Assert.Equal("Return.InvalidEvidence", nullRes.Error.Code);

        var wsDesc = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, "   ", [mediaId])]);
        var wsRes = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), wsDesc, "op-wsdesc");
        Assert.Equal("Return.InvalidEvidence", wsRes.Error.Code);
    }

    [Fact]
    public async Task DamagedUnit_MissingMedia_IsRejected()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();

        var noMedia = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, "Broken screen", [])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), noMedia, "op-nomedia");
        Assert.Equal("Return.InvalidEvidence", result.Error.Code);
    }

    [Fact]
    public async Task MediaVerification_WrongUploader_IsRejected()
    {
        var (service, _, item, media) = CreateService();
        var unit = item.UnitAssignments.First();
        var mediaId = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, renterId.ToString(), "Ready")));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready")));

        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, "Damaged housing", [mediaId])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-wrongowner");
        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_UPLOADER_MISMATCH", result.Error.Code);
    }

    [Fact]
    public async Task MediaVerification_PublicVisibility_IsRejected()
    {
        var (service, _, item, media) = CreateService();
        var unit = item.UnitAssignments.First();
        var mediaId = Guid.NewGuid();

        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, item.SupplierId.ToString(), "Ready")));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Public", "Ready")));

        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, "Scratched", [mediaId])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-public");
        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_MUST_BE_PRIVATE", result.Error.Code);
    }

    [Fact]
    public async Task MediaVerification_NotReady_IsRejected()
    {
        var (service, _, item, media) = CreateService();
        var unit = item.UnitAssignments.First();
        var mediaId = Guid.NewGuid();

        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, item.SupplierId.ToString(), "Pending")));

        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Damaged, "Scratched", [mediaId])]);
        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-notready");
        Assert.True(result.IsFailure);
        Assert.Equal("MEDIA_NOT_READY", result.Error.Code);
    }

    [Fact]
    public async Task BatchFailure_IsAtomic()
    {
        var (service, _, item, media) = CreateService();
        var u1 = item.UnitAssignments.First();
        var u2 = item.UnitAssignments.Skip(1).First();
        var mediaId = Guid.NewGuid();

        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, item.SupplierId.ToString(), "Ready")));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready")));

        // u1 is valid NORMAL, but u2 is DAMAGED without media -> entire batch fails
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [
            new(u1.Id, UnitCondition.Normal, "Good", []),
            new(u2.Id, UnitCondition.Damaged, "Cracked", [])
        ]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-atomic");
        Assert.True(result.IsFailure);
        Assert.Empty(item.ReturnRecords);
        Assert.Null(u1.ReturnRecordId);
        Assert.Null(u2.ReturnRecordId);
    }

    [Fact]
    public async Task NormalReturn_WithoutDescriptionOrMedia_Succeeds()
    {
        var (service, _, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        var request = new RecordReturnRequest(Now.AddHours(-1), null, [new(unit.Id, UnitCondition.Normal, null, [])]);

        var result = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), request, "op-normal");
        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.NotNull(unit.ReturnRecordId);
        Assert.Equal(UnitCondition.Normal, unit.ReturnCondition);
    }

    [Fact]
    public async Task Replay_WithStoredReceipt_ReturnsStoredResponseEvenIfNewValidationWouldFail()
    {
        var (service, order, item, _) = CreateService();
        var unit = item.UnitAssignments.First();
        string key = "op-legacy-replay";

        // Legacy request: DAMAGED with empty mediaIds
        var legacyReq = new RecordReturnRequest(Now.AddHours(-1), "Old receipt", [new(unit.Id, UnitCondition.Damaged, "No media", [])]);
        var payload = JsonSerializer.Serialize(new
        {
            ItemId = item.Id,
            SupplierId = item.SupplierId,
            ActualReturnAt = legacyReq.ActualReturnAt.ToUniversalTime(),
            legacyReq.Note,
            Units = legacyReq.Units.OrderBy(u => u.RentalUnitAssignmentId).Select(u => new
            {
                u.RentalUnitAssignmentId,
                u.Condition,
                u.Description,
                MediaIds = u.MediaIds.OrderBy(id => id)
            })
        });
        string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));

        var storedResponse = new RecordReturnResponse(Guid.NewGuid(), Now.AddHours(-2), 1, RentalItemStatus.InRental, null);
        var receipt = new RentalOperationReceipt(Guid.NewGuid(), order.Id, key, "RecordReturn", fingerprint, true, null, null,
            Now.AddHours(-2), targetItemId: item.Id, payloadJson: payload, apiResponseJson: JsonSerializer.Serialize(storedResponse));
        order.AddOperationReceipt(receipt);

        var replay = await service.RecordReturnAsync(item.Id, SupplierCaller(item.SupplierId), legacyReq, key);
        Assert.True(replay.IsSuccess);
        Assert.Equal(storedResponse.ReturnRecordId, replay.Value.ReturnRecordId);
    }
}
