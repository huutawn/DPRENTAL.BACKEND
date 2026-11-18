using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.Orders;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.SupplierDecision;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Supplier;

/// <summary>
/// Supplier bàn giao hai máy đầm của Bình cho An: bằng chứng chỉ thành bản ghi bàn giao khi Inventory xác nhận.
/// </summary>
public sealed class HandoverEvidenceTests
{
    private readonly string _dbName = $"HandoverEvidence_{Guid.NewGuid():N}";
    private readonly Guid _binh = Guid.NewGuid();
    private readonly AdjustableTimeProvider _clock = new(OrderTestData.Now.AddDays(1));
    private readonly InventoryReplySimulator _replies;
    private readonly Mock<IRentalMediaClient> _media = new();
    private RentalItem _item = null!;

    public HandoverEvidenceTests()
    {
        _replies = new InventoryReplySimulator(NewContext, _clock);
        _media.Setup(m => m.VerifyMediaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Result.Success(new MediaVerifyDto(id, _binh.ToString(), "Ready")));
        _media.Setup(m => m.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Result.Success(new MediaMetadataDto(id, "Private", "Ready")));
    }

    [Fact]
    public async Task NewHandoverAfterAutoExpiryDeadline_IsRejectedBeforeInventoryCommand()
    {
        await SeedAsync();
        _clock.UtcNow = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.FromHours(7));

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(
            _item.Id, Request(), "late-handover-1", OrderTestData.User(_binh));

        Assert.True(result.IsFailure);
        Assert.Equal("HANDOVER_DEADLINE_PASSED", result.Error.Code);
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver));
    }

    [Fact]
    public async Task Handover_WithoutConfirmation_IsRejectedAndNothingIsSent()
    {
        await SeedAsync();

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(
            _item.Id, Request(confirmed: false), "handover-1", OrderTestData.User(_binh));

        Assert.Equal("HANDOVER_NOT_CONFIRMED", result.Error.Code);
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver));
    }

    [Fact]
    public async Task Handover_WithMediaUploadedBySomeoneElse_IsRejectedAndNothingIsSent()
    {
        await SeedAsync();
        Guid stranger = Guid.NewGuid();
        _media.Setup(m => m.VerifyMediaAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Result.Success(new MediaVerifyDto(id, stranger.ToString(), "Ready")));

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(
            _item.Id, Request(), "handover-1", OrderTestData.User(_binh));

        Assert.Equal(MediaContractSpec.ErrorCodes.MediaUploaderMismatch, result.Error.Code);
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver));
        await using RentalServiceDbContext context = NewContext();
        Assert.Empty(await context.RentalOperationReceipts.Where(r => r.Action == "MarkAssetsHandedOver").ToListAsync());
    }

    [Fact]
    public async Task Handover_WithAMissingUnit_IsRejected()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with { Units = [Request().Units[0]] };

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(
            _item.Id, request, "handover-1", OrderTestData.User(_binh));

        Assert.Equal("INVALID_UNITS", result.Error.Code);
    }

    [Fact]
    public async Task Handover_KeepsEvidenceInTheReceiptUntilInventoryConfirms_AndBlocksASecondSubmission()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, "handover-1", OrderTestData.User(_binh));
        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, "handover-1", OrderTestData.User(_binh));
        Result<HandoverItemResponse> otherPayload = await Service().HandoverItemAsync(
            _item.Id, request with { Note = "Khác" }, "handover-1", OrderTestData.User(_binh));
        Result<HandoverItemResponse> secondKey = await Service().HandoverItemAsync(_item.Id, request, "handover-2", OrderTestData.User(_binh));

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Null(first.Value.HandoverRecordId);
        Assert.Equal("Processing", first.Value.Status);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal("IDEMPOTENCY_CONFLICT", otherPayload.Error.Code);
        Assert.Equal("HANDOVER_IN_PROGRESS", secondKey.Error.Code);
        Assert.Single(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver));

        await using RentalServiceDbContext context = NewContext();
        Assert.Empty(await context.HandoverRecords.ToListAsync());
        RentalItem item = await context.RentalItems.Include(i => i.UnitAssignments).SingleAsync();
        Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status);
        Assert.All(item.UnitAssignments, a => Assert.Empty(a.HandoverMediaIds));
        RentalOperationReceipt receipt = await context.RentalOperationReceipts.SingleAsync(r => r.Action == "MarkAssetsHandedOver");
        Assert.NotNull(HandoverEvidenceSnapshot.TryParse(receipt.PayloadJson));
    }

    [Fact]
    public async Task InventoryConfirms_CreatesTheImmutableRecordWithEvidencePerUnit_AndStartsTheRental()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "handover-1", OrderTestData.User(_binh))).IsSuccess);

        await _replies.SucceedAllAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver);

        await using RentalServiceDbContext context = NewContext();
        RentalItem item = await context.RentalItems
            .Include(i => i.UnitAssignments).Include(i => i.HandoverRecord).SingleAsync();
        Assert.Equal(RentalItemStatus.InRental, item.Status);
        HandoverRecord record = Assert.IsType<HandoverRecord>(item.HandoverRecord);
        Assert.Equal(request.ActualHandoverAt, record.ActualHandoverAt);
        Assert.Equal(request.Note, record.Note);
        Assert.Equal(_binh, record.SubmittedById);
        Assert.All(item.UnitAssignments, a =>
        {
            Assert.Equal(RentalUnitAssignmentStatus.HandedOver, a.Status);
            Assert.Equal(record.Id, a.HandoverRecordId);
            Assert.Equal(request.Units.Single(u => u.AssignmentId == a.Id).MediaIds, a.HandoverMediaIds);
        });

        // Bàn giao xong thì không bàn giao lại được.
        Result<HandoverItemResponse> again = await Service().HandoverItemAsync(_item.Id, request, "handover-3", OrderTestData.User(_binh));
        Assert.Equal("INVALID_STATUS", again.Error.Code);
    }

    [Fact]
    public async Task DuplicateInventoryConfirmation_DoesNotRecordTheHandoverTwice()
    {
        await SeedAsync();
        Assert.True((await Service().HandoverItemAsync(_item.Id, Request(), "handover-1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();
        Assert.True((await _replies.SucceedAsync(command)).IsSuccess);

        Result redelivered = await _replies.RedeliverSuccessAsync(command);

        Assert.True(redelivered.IsSuccess, redelivered.Error.Code);
        await using RentalServiceDbContext context = NewContext();
        Assert.Single(await context.HandoverRecords.ToListAsync());
        Assert.Equal(1, await context.RentalStateTransitions.CountAsync(t => t.Action == "MarkAssetsHandedOver"));
        Assert.Equal(RentalItemStatus.InRental, (await context.RentalItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task InventoryRejects_LeavesNoRecord_AndTheSupplierCanSubmitAgainWithANewKey()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "handover-1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();

        Result rejected = await _replies.RejectAsync(command, "ASSET_NOT_ALLOCATED", "Máy chưa được cấp phát");

        Assert.True(rejected.IsSuccess, rejected.Error.Code);
        await using (RentalServiceDbContext context = NewContext())
        {
            Assert.Empty(await context.HandoverRecords.ToListAsync());
            RentalItem item = await context.RentalItems.Include(i => i.UnitAssignments).SingleAsync();
            Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status);
            Assert.All(item.UnitAssignments, a => Assert.Null(a.HandoverRecordId));
        }

        Result<HandoverItemResponse> retry = await Service().HandoverItemAsync(_item.Id, request, "handover-2", OrderTestData.User(_binh));
        Assert.True(retry.IsSuccess, retry.Error.Code);
        InventoryCommand retryCommand = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();
        Assert.True((await _replies.SucceedAsync(retryCommand)).IsSuccess);

        await using RentalServiceDbContext check = NewContext();
        Assert.Single(await check.HandoverRecords.ToListAsync());
        Assert.Equal(RentalItemStatus.InRental, (await check.RentalItems.SingleAsync()).Status);
    }

    [Fact]
    public async Task HandoverSuccess_DoesNotReleaseAllocation_EquipmentRemainsAllocated()
    {
        // Thiết bị công trường sau khi giao cho người thuê phải tiếp tục được giữ chỗ, không phát lệnh ReleaseAllocation
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "handover-alloc-1", OrderTestData.User(_binh))).IsSuccess);

        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();
        Assert.True((await _replies.SucceedAsync(command)).IsSuccess);

        // Sau khi hoàn tất bàn giao: không được có bất kỳ lệnh ReleaseAllocation nào
        IReadOnlyList<InventoryCommand> releaseCommands = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.ReleaseAllocation);
        Assert.Empty(releaseCommands);

        await using RentalServiceDbContext context = NewContext();
        RentalItem item = await context.RentalItems.SingleAsync(i => i.Id == _item.Id);
        Assert.Equal(RentalItemStatus.InRental, item.Status);
    }

    [Fact]
    public async Task MultiItemOrder_RollsUpToInRental_OnlyWhenAllActiveItemsAreHandedOver()
    {
        // Đơn hàng thiết bị công trường gồm 2 mục thuê của cùng supplier Bình: Máy đầm cóc và Máy lu rung
        RentalOrder order = OrderTestData.Paid(
            OrderTestData.Order(
                Guid.NewGuid(),
                "DH-2026-MULTI-01",
                OrderTestData.Now,
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m),
                OrderTestData.Line(_binh, "Máy lu rung Sakai", "Máy lu", "Sakai", 1, 1_500_000m, 10_000_000m)),
            OrderTestData.Now.AddMinutes(5));

        RentalItem item1 = order.Items.First();
        RentalItem item2 = order.Items.Last();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item1, RentalItemStatus.AwaitingHandover);
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item2, RentalItemStatus.AwaitingHandover);

        await using (RentalServiceDbContext setup = NewContext())
        {
            setup.RentalOrders.Add(order);
            await setup.SaveChangesAsync();
        }

        // Bàn giao mục thứ nhất: Máy đầm cóc
        HandoverItemRequest req1 = new(
            OrderTestData.Now.AddDays(1).AddHours(-2),
            "Bàn giao máy đầm tại công trường",
            item1.UnitAssignments.Select(a => new HandoverUnitObservationRequest(a.Id, UnitCondition.Normal, "Hoạt động tốt", [Guid.NewGuid()])).ToList(),
            true);
        Assert.True((await Service().HandoverItemAsync(item1.Id, req1, "handover-multi-item1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand cmd1 = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver))
            .Single(c => c.RentalItemId == item1.Id);
        Assert.True((await _replies.SucceedAsync(cmd1)).IsSuccess);

        // Sau khi giao Máy đầm: item1 sang InRental nhưng toàn đơn vẫn là AwaitingHandover vì Máy lu chưa giao
        await using (RentalServiceDbContext checkStep1 = NewContext())
        {
            RentalOrder loadedOrder = await checkStep1.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            Assert.Equal(RentalOrderStatus.AwaitingHandover, loadedOrder.Status);
            Assert.Equal(RentalItemStatus.InRental, loadedOrder.Items.Single(i => i.Id == item1.Id).Status);
            Assert.Equal(RentalItemStatus.AwaitingHandover, loadedOrder.Items.Single(i => i.Id == item2.Id).Status);
        }

        // Bàn giao mục thứ hai: Máy lu rung
        HandoverItemRequest req2 = new(
            OrderTestData.Now.AddDays(1).AddHours(-1),
            "Bàn giao máy lu tại công trường",
            item2.UnitAssignments.Select(a => new HandoverUnitObservationRequest(a.Id, UnitCondition.Normal, "Máy mới hoạt động êm", [Guid.NewGuid()])).ToList(),
            true);
        Assert.True((await Service().HandoverItemAsync(item2.Id, req2, "handover-multi-item2", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand cmd2 = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver))
            .Single(c => c.RentalItemId == item2.Id);
        Assert.True((await _replies.SucceedAsync(cmd2)).IsSuccess);

        // Khi cả 2 thiết bị công trường đều đã bàn giao xong: đơn hàng chính thức chuyển sang InRental
        await using (RentalServiceDbContext checkStep2 = NewContext())
        {
            RentalOrder loadedOrder = await checkStep2.RentalOrders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            Assert.Equal(RentalOrderStatus.InRental, loadedOrder.Status);
            Assert.All(loadedOrder.Items, i => Assert.Equal(RentalItemStatus.InRental, i.Status));
        }
    }

    [Fact]
    public async Task InventoryConfirms_WhenItemIsAlreadyInTerminalState_DoesNotOverrideStatus()
    {
        // Trường hợp mục thuê bị Expired hoặc Rejected trước khi tin nhắn Inventory tới
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "handover-term-1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();

        // Giả lập trạng thái mục thuê bị chuyển sang Expired (ví dụ do sự cố hết hạn / hủy)
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalItem item = await context.RentalItems.SingleAsync(i => i.Id == _item.Id);
            typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(item, RentalItemStatus.Expired);
            await context.SaveChangesAsync();
        }

        // Kết quả từ Inventory trả về sau đó không được ghi đè trạng thái Expired thành InRental
        Result replyResult = await _replies.SucceedAsync(command);
        Assert.True(replyResult.IsSuccess);

        await using (RentalServiceDbContext check = NewContext())
        {
            RentalItem item = await check.RentalItems.SingleAsync(i => i.Id == _item.Id);
            Assert.Equal(RentalItemStatus.Expired, item.Status);
            Assert.Null(item.HandoverRecord);
        }
    }

    [Fact]
    public async Task InventoryConfirms_WhenEvidenceSnapshotIsCorrupted_RequiresManualReconciliation()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "corrupt-snap-1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();

        await using (RentalServiceDbContext context = NewContext())
        {
            var receipt = await context.RentalOperationReceipts.SingleAsync(r => r.OperationId == "corrupt-snap-1");
            typeof(RentalOperationReceipt).GetProperty(nameof(RentalOperationReceipt.PayloadJson))!.SetValue(receipt, "{\"corrupted\":true}");
            await context.SaveChangesAsync();
        }

        Result replyResult = await _replies.SucceedAsync(command);
        Assert.True(replyResult.IsSuccess);

        await using (RentalServiceDbContext check = NewContext())
        {
            var receipt = await check.RentalOperationReceipts.SingleAsync(r => r.OperationId == "corrupt-snap-1");
            Assert.Equal(RentalOperationReceipt.StatusManualReconciliationRequired, receipt.Status);
            RentalItem item = await check.RentalItems.SingleAsync(i => i.Id == _item.Id);
            Assert.Equal(RentalItemStatus.AwaitingHandover, item.Status);
            Assert.Null(item.HandoverRecord);
        }
    }

    [Fact]
    public async Task InventoryConfirms_ConflictingTerminalResult_IsRejectedWithConflict()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();
        Assert.True((await Service().HandoverItemAsync(_item.Id, request, "conflict-term-1", OrderTestData.User(_binh))).IsSuccess);
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();

        Result firstSuccess = await _replies.SucceedAsync(command);
        Assert.True(firstSuccess.IsSuccess);
        // Send a contradictory result (rejection for already succeeded receipt)
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        Result secondConflict = await _replies.RejectAsync(command, "CONFLICT_ERROR", "Contradictory result");
        Assert.True(secondConflict.IsSuccess);

        await using (RentalServiceDbContext check = NewContext())
        {
            var lastMsg = await check.RentalInboxMessages.OrderByDescending(m => m.ReceivedAt).FirstAsync();
            Assert.Equal(RentalInboxMessage.StatusDeadLettered, lastMsg.Status);
            Assert.Contains("INVENTORY_RESULT_CONFLICT", lastMsg.ErrorMessage);
        }
    }

    [Fact]
    public async Task Handover_WithActualHandoverAtInFuture_EvenSmallFraction_IsRejected()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with { ActualHandoverAt = _clock.UtcNow.AddSeconds(1) };
        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, request, "future-1", OrderTestData.User(_binh));
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_HANDOVER_TIME", result.Error.Code);
        Assert.Empty(await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver));
    }

    [Fact]
    public async Task Handover_WithActualHandoverAtExactlyNow_IsAccepted()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with { ActualHandoverAt = _clock.UtcNow };
        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, request, "now-1", OrderTestData.User(_binh));
        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Fact]
    public async Task Handover_WithDefaultHandoverAt_IsRejected()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with { ActualHandoverAt = default };
        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, request, "default-time", OrderTestData.User(_binh));
        Assert.Equal("INVALID_HANDOVER_TIME", result.Error.Code);
    }

    [Fact]
    public async Task Handover_WithWrongSupplier_IsRejectedAndNoReceiptCreated()
    {
        await SeedAsync();
        Guid stranger = Guid.NewGuid();
        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, Request(), "stranger-1", OrderTestData.User(stranger));
        Assert.Equal(RentalErrors.ItemNotFound.Code, result.Error.Code);
        await using RentalServiceDbContext context = NewContext();
        Assert.Empty(await context.RentalOperationReceipts.Where(r => r.Action == "MarkAssetsHandedOver").ToListAsync());
    }

    [Fact]
    public async Task Handover_WhenInManualReconciliation_BlocksNewKeyWithHandoverInProgress()
    {
        await SeedAsync();
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalOrder order = await context.RentalOrders.SingleAsync();
            var receipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(), order.Id, "op-old", "MarkAssetsHandedOver", "fp",
                _clock.UtcNow, _item.Id, "{}");
            receipt.MarkManualReconciliationRequired(_clock.UtcNow, "Needs review");
            order.AddOperationReceipt(receipt);
            await context.SaveChangesAsync();
        }

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, Request(), "new-key", OrderTestData.User(_binh));
        Assert.Equal("HANDOVER_IN_PROGRESS", result.Error.Code);
    }

    [Fact]
    public async Task Handover_ReplaySucceededHandover_ReturnsExistingRecord()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();
        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, "replay-succ-1", OrderTestData.User(_binh));
        Assert.True(first.IsSuccess);

        await _replies.SucceedAllAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver);

        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, "replay-succ-1", OrderTestData.User(_binh));
        Assert.True(replay.IsSuccess);
        Assert.NotNull(replay.Value.HandoverRecordId);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, replay.Value.Status);
    }

    [Fact]
    public async Task Handover_WhenUnitQuantityMappingIncomplete_IsRejected()
    {
        await SeedAsync();
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalOrder order = await context.RentalOrders.Include(o => o.Items).ThenInclude(i => i.UnitAssignments).SingleAsync();
            RentalItem item = order.Items.Single();
            RentalUnitAssignment toRemove = item.UnitAssignments.First();
            context.RentalUnitAssignments.Remove(toRemove);
            await context.SaveChangesAsync();
        }

        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, Request(), "incomp-1", OrderTestData.User(_binh));
        Assert.Equal("INVALID_ASSIGNMENTS", result.Error.Code);
    }

    [Fact]
    public async Task Handover_WhenRequestHasDuplicateAssignmentId_IsRejected()
    {
        await SeedAsync();
        var firstUnit = Request().Units[0];
        HandoverItemRequest request = Request() with { Units = [firstUnit, firstUnit] };
        Result<HandoverItemResponse> result = await Service().HandoverItemAsync(_item.Id, request, "dup-unit", OrderTestData.User(_binh));
        Assert.Equal("INVALID_UNITS", result.Error.Code);
    }

    [Fact]
    public async Task Handover_WhenActualRecipientOmitted_DefaultsToOrderSnapshot_AndIsIdempotent()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, "ho-default-1", OrderTestData.User(_binh));
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal("Nguyễn Văn An", first.Value.ActualRecipientName);
        Assert.Equal("0901234567", first.Value.ActualRecipientPhone);

        // Replay with same idempotency key and same payload is idempotent
        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, "ho-default-1", OrderTestData.User(_binh));
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal("Nguyễn Văn An", replay.Value.ActualRecipientName);
        Assert.Equal("0901234567", replay.Value.ActualRecipientPhone);

        // Check command payload
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();
        Assert.Equal("Nguyễn Văn An", command.Payload.GetProperty("actualRecipientName").GetString());
        Assert.Equal("0901234567", command.Payload.GetProperty("actualRecipientPhone").GetString());

        // Inventory confirms
        Assert.True((await _replies.SucceedAsync(command)).IsSuccess);

        await using RentalServiceDbContext context = NewContext();
        RentalItem item = await context.RentalItems
            .Include(i => i.UnitAssignments).Include(i => i.HandoverRecord).SingleAsync();
        HandoverRecord record = Assert.IsType<HandoverRecord>(item.HandoverRecord);
        Assert.Equal("Nguyễn Văn An", record.ActualRecipientName);
        Assert.Equal("0901234567", record.ActualRecipientPhone);

        var readModel = Dprental.RentalService.Application.Services.Orders.OrderQueryMapper.ToHandover(item);
        Assert.NotNull(readModel);
        Assert.Equal("Nguyễn Văn An", readModel.ActualRecipientName);
        Assert.Equal("0901234567", readModel.ActualRecipientPhone);
    }

    [Fact]
    public async Task Handover_WhenSupplierSpecifiesOtherRecipient_UsesSpecifiedRecipientWithoutChangingOrderOwnership()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with
        {
            ActualRecipientName = "Trần Thị B",
            ActualRecipientPhone = "0987654321"
        };

        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, "ho-other-1", OrderTestData.User(_binh));
        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.Equal("Trần Thị B", first.Value.ActualRecipientName);
        Assert.Equal("0987654321", first.Value.ActualRecipientPhone);

        // Retrying with different recipient on same key causes conflict
        HandoverItemRequest conflicting = request with { ActualRecipientName = "Lê Văn C" };
        Result<HandoverItemResponse> conflict = await Service().HandoverItemAsync(_item.Id, conflicting, "ho-other-1", OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);

        // Replay with identical payload succeeds
        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, "ho-other-1", OrderTestData.User(_binh));
        Assert.True(replay.IsSuccess, replay.Error.Code);

        // Verify order ownership unchanged
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalOrder order = await context.RentalOrders.SingleAsync();
            Assert.NotEqual(Guid.Empty, order.RenterId);
            Assert.Equal("Nguyễn Văn An", order.RecipientName);
            Assert.Equal("0901234567", order.RecipientPhone);
        }

        // Inventory confirms
        InventoryCommand command = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Single();
        Assert.Equal("Trần Thị B", command.Payload.GetProperty("actualRecipientName").GetString());
        Assert.Equal("0987654321", command.Payload.GetProperty("actualRecipientPhone").GetString());

        Assert.True((await _replies.SucceedAsync(command)).IsSuccess);

        await using (RentalServiceDbContext context = NewContext())
        {
            RentalItem item = await context.RentalItems
                .Include(i => i.UnitAssignments).Include(i => i.HandoverRecord).SingleAsync();
            HandoverRecord record = Assert.IsType<HandoverRecord>(item.HandoverRecord);
            Assert.Equal("Trần Thị B", record.ActualRecipientName);
            Assert.Equal("0987654321", record.ActualRecipientPhone);

            var readModel = Dprental.RentalService.Application.Services.Orders.OrderQueryMapper.ToHandover(item);
            Assert.NotNull(readModel);
            Assert.Equal("Trần Thị B", readModel.ActualRecipientName);
            Assert.Equal("0987654321", readModel.ActualRecipientPhone);
        }
    }

    [Fact]
    public async Task Handover_Validation_RejectsInvalidRecipientPhoneOrTooLongName()
    {
        await SeedAsync();

        HandoverItemRequest invalidPhone = Request() with { ActualRecipientPhone = "12345" };
        Result<HandoverItemResponse> res1 = await Service().HandoverItemAsync(_item.Id, invalidPhone, "val-phone", OrderTestData.User(_binh));
        Assert.Equal("INVALID_RECIPIENT_PHONE", res1.Error.Code);

        HandoverItemRequest invalidName = Request() with { ActualRecipientName = new string('A', 201) };
        Result<HandoverItemResponse> res2 = await Service().HandoverItemAsync(_item.Id, invalidName, "val-name", OrderTestData.User(_binh));
        Assert.Equal("INVALID_RECIPIENT_NAME", res2.Error.Code);
    }

    [Fact]
    public async Task HistoricalHandoverRecord_WithoutActualRecipient_RemainsReadable()
    {
        await SeedAsync();

        // Historical JSON without actualRecipientName and actualRecipientPhone
        string historicalJson = """
        {
            "submittedById": "11111111-1111-1111-1111-111111111111",
            "submittedAt": "2026-10-10T00:00:00+00:00",
            "actualHandoverAt": "2026-10-10T00:00:00+00:00",
            "note": "Historical note",
            "units": [
                {
                    "assignmentId": "22222222-2222-2222-2222-222222222222",
                    "condition": "Normal",
                    "note": null,
                    "mediaIds": ["33333333-3333-3333-3333-333333333333"]
                }
            ]
        }
        """;

        var parsed = HandoverEvidenceSnapshot.TryParse(historicalJson);
        Assert.NotNull(parsed);
        Assert.Null(parsed.ActualRecipientName);
        Assert.Null(parsed.ActualRecipientPhone);

        var historicalRecord = new HandoverRecord(
            Guid.NewGuid(),
            _item.Id,
            parsed.ActualHandoverAt,
            parsed.Note,
            parsed.SubmittedById,
            parsed.SubmittedAt,
            parsed.SubmittedAt);

        Assert.Null(historicalRecord.ActualRecipientName);
        Assert.Null(historicalRecord.ActualRecipientPhone);

        _item.SetHandoverRecord(historicalRecord);
        var readModel = Dprental.RentalService.Application.Services.Orders.OrderQueryMapper.ToHandover(_item);
        Assert.NotNull(readModel);
        Assert.Null(readModel.ActualRecipientName);
        Assert.Null(readModel.ActualRecipientPhone);
    }

    [Fact]
    public async Task LegacyReceipt_PendingAndSucceeded_SameKeyAndPayload_ReplaySucceedsWithoutNewCommand()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        // 1. Compute legacy fingerprint (pre-upgrade, without recipient fields)
        string legacyFp = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = _item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            request.Note,
            request.Confirmed,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        // Seed a legacy receipt in Pending status
        string opKey = "legacy-op-pending";
        var legacyReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), _item.RentalOrderId, opKey, "MarkAssetsHandedOver", legacyFp,
            OrderTestData.Now, _item.Id, "{}");

        await using (var context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == _item.RentalOrderId);
            order.AddOperationReceipt(legacyReceipt);
            await context.SaveChangesAsync();
        }

        int commandCountBefore = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Count;

        // Replay with no recipient in request -> should match legacy receipt and succeed
        Result<HandoverItemResponse> replayPending = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(replayPending.IsSuccess, replayPending.Error.Code);
        Assert.Equal("Pending", replayPending.Value.Status);
        Assert.Null(replayPending.Value.HandoverRecordId);

        // Verify NO new command was enqueued
        int commandCountAfter = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Count;
        Assert.Equal(commandCountBefore, commandCountAfter);

        // 2. Now test with Succeeded legacy receipt
        string opKeySucceeded = "legacy-op-succeeded";
        Guid handoverRecordId = Guid.NewGuid();
        var succeededReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), _item.RentalOrderId, opKeySucceeded, "MarkAssetsHandedOver", legacyFp,
            OrderTestData.Now, _item.Id, "{}");
        succeededReceipt.MarkSucceeded(OrderTestData.Now.AddMinutes(1), "{}");

        var historicalRecord = new HandoverRecord(
            handoverRecordId, _item.Id, request.ActualHandoverAt, request.Note, _binh, OrderTestData.Now, OrderTestData.Now);

        await using (var context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.OperationReceipts).Include(o => o.Items).SingleAsync(o => o.Id == _item.RentalOrderId);
            order.AddOperationReceipt(succeededReceipt);
            var item = order.Items.Single(i => i.Id == _item.Id);
            item.SetHandoverRecord(historicalRecord);
            context.HandoverRecords.Add(historicalRecord);
            await context.SaveChangesAsync();
        }

        Result<HandoverItemResponse> replaySucceeded = await Service().HandoverItemAsync(_item.Id, request, opKeySucceeded, OrderTestData.User(_binh));
        Assert.True(replaySucceeded.IsSuccess, replaySucceeded.Error.Code);
        Assert.Equal("Succeeded", replaySucceeded.Value.Status);
        Assert.Equal(handoverRecordId, replaySucceeded.Value.HandoverRecordId);
        Assert.Null(replaySucceeded.Value.ActualRecipientName);
        Assert.Null(replaySucceeded.Value.ActualRecipientPhone);

        // Verify still no new commands enqueued
        int commandCountFinal = (await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver)).Count;
        Assert.Equal(commandCountBefore, commandCountFinal);
    }

    [Fact]
    public async Task LegacyReceipt_WhenPayloadChangesTimeOrNoteOrUnits_ReturnsConflict()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        string legacyFp = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = _item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            request.Note,
            request.Confirmed,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        string opKey = "legacy-op-conflict-test";
        var legacyReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), _item.RentalOrderId, opKey, "MarkAssetsHandedOver", legacyFp,
            OrderTestData.Now, _item.Id, "{}");

        await using (var context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == _item.RentalOrderId);
            order.AddOperationReceipt(legacyReceipt);
            await context.SaveChangesAsync();
        }

        // 1. Change time
        HandoverItemRequest changedTime = request with { ActualHandoverAt = request.ActualHandoverAt.AddMinutes(-10) };
        Result<HandoverItemResponse> resTime = await Service().HandoverItemAsync(_item.Id, changedTime, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resTime.Error.Code);

        // 2. Change note
        HandoverItemRequest changedNote = request with { Note = "Ghi chú hoàn toàn khác" };
        Result<HandoverItemResponse> resNote = await Service().HandoverItemAsync(_item.Id, changedNote, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resNote.Error.Code);

        // 3. Change unit condition
        var changedUnit = request.Units[0] with { Condition = UnitCondition.Damaged };
        HandoverItemRequest changedUnits = request with { Units = [changedUnit, request.Units[1]] };
        Result<HandoverItemResponse> resUnits = await Service().HandoverItemAsync(_item.Id, changedUnits, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resUnits.Error.Code);
    }

    [Fact]
    public async Task LegacyReceipt_WhenRequestProvidesNewRecipient_ReturnsConflict()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        string legacyFp = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = _item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            request.Note,
            request.Confirmed,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        string opKey = "legacy-op-new-recipient-test";
        var legacyReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), _item.RentalOrderId, opKey, "MarkAssetsHandedOver", legacyFp,
            OrderTestData.Now, _item.Id, "{}");

        await using (var context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.OperationReceipts).SingleAsync(o => o.Id == _item.RentalOrderId);
            order.AddOperationReceipt(legacyReceipt);
            await context.SaveChangesAsync();
        }

        // When request specifies an actual recipient name, it MUST NOT match legacy receipt
        HandoverItemRequest reqWithName = request with { ActualRecipientName = "Người mới nhận máy" };
        Result<HandoverItemResponse> resName = await Service().HandoverItemAsync(_item.Id, reqWithName, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resName.Error.Code);

        // When request specifies an actual recipient phone, it MUST NOT match legacy receipt
        HandoverItemRequest reqWithPhone = request with { ActualRecipientPhone = "0987654321" };
        Result<HandoverItemResponse> resPhone = await Service().HandoverItemAsync(_item.Id, reqWithPhone, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resPhone.Error.Code);
    }

    [Fact]
    public async Task NewReceipt_WhenNameOrPhoneChanges_ReturnsConflict()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with
        {
            ActualRecipientName = "Nguyễn Văn Nhận",
            ActualRecipientPhone = "0912345678"
        };

        string opKey = "new-receipt-conflict-test";
        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(first.IsSuccess, first.Error.Code);

        // Replay with identical recipient succeeds
        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(replay.IsSuccess, replay.Error.Code);

        // Changed name -> conflict
        HandoverItemRequest changedName = request with { ActualRecipientName = "Trần Văn Khác" };
        Result<HandoverItemResponse> resName = await Service().HandoverItemAsync(_item.Id, changedName, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resName.Error.Code);

        // Changed phone -> conflict
        HandoverItemRequest changedPhone = request with { ActualRecipientPhone = "0987654321" };
        Result<HandoverItemResponse> resPhone = await Service().HandoverItemAsync(_item.Id, changedPhone, opKey, OrderTestData.User(_binh));
        Assert.Equal("IDEMPOTENCY_CONFLICT", resPhone.Error.Code);
    }

    [Fact]
    public async Task ConcurrentReplay_DoesNotCreateSecondReceiptOrCommand()
    {
        await SeedAsync();
        HandoverItemRequest request = Request() with
        {
            ActualRecipientName = "Người Nhận",
            ActualRecipientPhone = "0901234567"
        };

        string opKey = "concurrent-replay-test";

        // First call
        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(first.IsSuccess, first.Error.Code);

        // Run 2 concurrent replays with the same key and payload
        Task<Result<HandoverItemResponse>> t1 = Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Task<Result<HandoverItemResponse>> t2 = Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));

        Result<HandoverItemResponse>[] results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.True(r.IsSuccess));

        // Check receipts in DB: exactly 1 receipt
        await using (var context = NewContext())
        {
            var receipts = await context.RentalOperationReceipts.Where(r => r.OperationId == opKey).ToListAsync();
            Assert.Single(receipts);
        }

        // Check commands in Outbox: exactly 1 command
        var pending = await _replies.PendingCommandsAsync(KafkaContractSpec.CommandTypes.MarkAssetsHandedOver);
        Assert.Single(pending);
    }

    [Fact]
    public async Task HistoricalHandoverRecord_ReplayDoesNotModifyHistoricalData()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        string legacyFp = RentalOperationExecutor.ComputeFingerprint(new
        {
            Action = "MarkAssetsHandedOver",
            ItemId = _item.Id,
            ActualHandoverAt = request.ActualHandoverAt,
            request.Note,
            request.Confirmed,
            Units = request.Units.OrderBy(u => u.AssignmentId).Select(u => new
            {
                u.AssignmentId,
                u.Condition,
                u.Note,
                MediaIds = (u.MediaIds ?? []).OrderBy(m => m).ToList()
            }).ToList()
        });

        string opKey = "legacy-replay-no-modify";
        Guid handoverRecordId = Guid.NewGuid();
        var succeededReceipt = RentalOperationReceipt.CreatePending(
            Guid.NewGuid(), _item.RentalOrderId, opKey, "MarkAssetsHandedOver", legacyFp,
            OrderTestData.Now, _item.Id, "{}");
        succeededReceipt.MarkSucceeded(OrderTestData.Now.AddMinutes(1), "{}");

        var historicalRecord = new HandoverRecord(
            handoverRecordId, _item.Id, request.ActualHandoverAt, request.Note, _binh, OrderTestData.Now, OrderTestData.Now,
            actualRecipientName: null, actualRecipientPhone: null);

        await using (var context = NewContext())
        {
            var order = await context.RentalOrders.Include(o => o.OperationReceipts).Include(o => o.Items).SingleAsync(o => o.Id == _item.RentalOrderId);
            order.AddOperationReceipt(succeededReceipt);
            var item = order.Items.Single(i => i.Id == _item.Id);
            item.SetHandoverRecord(historicalRecord);
            context.HandoverRecords.Add(historicalRecord);
            await context.SaveChangesAsync();
        }

        Result<HandoverItemResponse> replay = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(replay.IsSuccess, replay.Error.Code);

        // Verify that historical record in DB was NOT modified
        await using (var context = NewContext())
        {
            var recordInDb = await context.HandoverRecords.SingleAsync(h => h.Id == handoverRecordId);
            Assert.Null(recordInDb.ActualRecipientName);
            Assert.Null(recordInDb.ActualRecipientPhone);
        }
    }

    [Fact]
    public async Task OtherSupplier_CannotReplayReceipt()
    {
        await SeedAsync();
        HandoverItemRequest request = Request();

        string opKey = "supplier-isolation-test";
        Result<HandoverItemResponse> first = await Service().HandoverItemAsync(_item.Id, request, opKey, OrderTestData.User(_binh));
        Assert.True(first.IsSuccess, first.Error.Code);

        // Different supplier attempts to replay
        Guid otherSupplierId = Guid.NewGuid();
        Result<HandoverItemResponse> otherSupplierResult = await Service().HandoverItemAsync(
            _item.Id, request, opKey, OrderTestData.User(otherSupplierId));

        Assert.True(otherSupplierResult.IsFailure);
        Assert.Equal(RentalErrors.ItemNotFound.Code, otherSupplierResult.Error.Code);
    }

    private HandoverItemRequest Request(bool confirmed = true) => new(
        OrderTestData.Now.AddDays(1).AddHours(-2),
        "Giao tại công trường, máy chạy tốt",
        _item.UnitAssignments
            .Select(a => new HandoverUnitObservationRequest(a.Id, UnitCondition.Normal, "Bình thường", [Guid.NewGuid(), Guid.NewGuid()]))
            .ToList(),
        confirmed);

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private SupplierDecisionService Service()
    {
        RentalServiceDbContext context = NewContext();
        RentalOrderRepository orders = new(context);
        OutboxRepository outbox = new(context);
        FakeOrderLockManager locks = new();
        RentalOperationExecutor executor = new(
            orders,
            new OperationReceiptRepository(context),
            outbox,
            locks,
            new FakeInventoryRentalClient(),
            new FakePaymentRentalClient(),
            context,
            new ConfigurationBuilder().Build(),
            NullLogger<RentalOperationExecutor>.Instance,
            _clock);
        InventoryCommandProducer commands = new(
            outbox, Microsoft.Extensions.Options.Options.Create(new KafkaOptions { Enabled = true }), _clock);
        return new SupplierDecisionService(
            orders, executor, locks, commands, context, _clock, NullLogger<SupplierDecisionService>.Instance, _media.Object);
    }

    private async Task SeedAsync()
    {
        RentalOrder order = OrderTestData.Paid(
            OrderTestData.Order(
                Guid.NewGuid(),
                "DH-2026-000030",
                OrderTestData.Now,
                OrderTestData.Line(_binh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 2, 300_000m, 2_000_000m)),
            OrderTestData.Now.AddMinutes(5));
        _item = order.Items.Single();
        typeof(RentalItem).GetProperty(nameof(RentalItem.Status))!.SetValue(_item, RentalItemStatus.AwaitingHandover);

        await using RentalServiceDbContext context = NewContext();
        context.RentalOrders.Add(order);
        await context.SaveChangesAsync();
    }
}
