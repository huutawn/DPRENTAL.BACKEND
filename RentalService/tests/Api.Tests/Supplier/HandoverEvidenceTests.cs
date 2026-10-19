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
