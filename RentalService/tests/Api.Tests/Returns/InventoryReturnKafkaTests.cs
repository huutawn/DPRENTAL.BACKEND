using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class InventoryReturnKafkaTests
{
    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RecordReturn_KafkaMode_QueuesCommandWithReturnInOneTransaction()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
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
        ApplyReturnedUnitsCommandPayload? sent = null;
        var producer = new Mock<IInventoryCommandProducer>();
        producer.Setup(p => p.EnqueueApplyReturnedUnitsAsync(It.IsAny<ApplyReturnedUnitsCommandPayload>(),
                It.IsAny<CancellationToken>()))
            .Callback((ApplyReturnedUnitsCommandPayload c, CancellationToken _) => sent = c)
            .ReturnsAsync(new ProducerEnqueueResult(Guid.NewGuid(), false,
                KafkaContractSpec.CommandTypes.ApplyReturnedUnits,
                KafkaContractSpec.Topics.InventoryCommands, order.Id.ToString("D")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:Enabled"] = "true",
            ["Rental:InventoryReturn:Enabled"] = "true",
            ["Rental:InventoryReturn:Transport"] = "Kafka"
        }).Build();
        var service = new ReturnService(repository.Object, locks.Object, uow.Object,
            Mock.Of<IRentalMediaClient>(), new FakeTimeProvider(Now), new FakeRentalEventProducer(),
            config, producer.Object);
        var caller = RentalCallerContext.FromClaims(
            [new KeyValuePair<string, string>("sub", item.SupplierId.ToString("D"))]);

        var result = await service.RecordReturnAsync(item.Id, caller,
            new RecordReturnRequest(Now.AddHours(-1), null,
                [new ReturnUnitRequest(unit.Id, UnitCondition.Normal, null, [])]), "return-key");

        Assert.True(result.IsSuccess);
        Assert.NotNull(sent);
        Assert.Equal(unit.AllocationId, Assert.Single(sent.Units).AllocationId);
        Assert.Equal("NORMAL", sent.Units[0].Condition);
        Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.ApplyReturnedUnitsAction);
        Assert.DoesNotContain(order.OperationReceipts, r => r.Action == RentalItem.MarkReturnedAction);
        tx.Verify(t => t.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        producer.Verify(p => p.EnqueueApplyReturnedUnitsAsync(It.IsAny<ApplyReturnedUnitsCommandPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Producer_UsesInventoryAllocationTopicAndOrderPartition()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var record = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Normal, null, [])], Now, item.SupplierId);
        Assert.True(record.IsSuccess);
        var command = ReturnService.ScheduleKafkaInventoryReturn(order, item, record.Value, Now);
        string? topic = null;
        string? partition = null;
        var writer = new Mock<IKafkaOutboxWriter>();
        writer.Setup(w => w.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<KafkaMessageEnvelope<ApplyReturnedUnitsCommandPayload>>(), It.IsAny<CancellationToken>()))
            .Callback((string t, string p, KafkaMessageEnvelope<ApplyReturnedUnitsCommandPayload> _, CancellationToken _) =>
            {
                topic = t;
                partition = p;
            })
            .ReturnsAsync(new ProducerEnqueueResult(Guid.NewGuid(), false,
                KafkaContractSpec.CommandTypes.ApplyReturnedUnits,
                KafkaContractSpec.Topics.InventoryCommands, order.Id.ToString("D")));
        var producer = new InventoryCommandProducer(writer.Object,
            Options.Create(new KafkaOptions()), new FakeTimeProvider(Now));

        Assert.True((await producer.EnqueueApplyReturnedUnitsAsync(command)).IsSuccess);
        Assert.Equal(KafkaContractSpec.Topics.InventoryCommands, topic);
        Assert.Equal(order.Id.ToString("D"), partition);
    }

    [Fact]
    public async Task MixedReturn_ConfirmsOnlyReturnedUnits_AfterProtectedInventoryResult()
    {
        var order = ReturnTests.NewOrder(quantity: 3);
        var item = order.Items.Single();
        var normal = item.UnitAssignments.First();
        var damaged = item.UnitAssignments.Skip(1).First();
        var unreturned = item.UnitAssignments.Last();
        var record = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(normal.Id, UnitCondition.Normal, null, []),
             new ReturnUnitData(damaged.Id, UnitCondition.Damaged, "scratch", [])], Now, item.SupplierId);
        Assert.True(record.IsSuccess);
        var command = ReturnService.ScheduleKafkaInventoryReturn(order, item, record.Value, Now);
        Assert.Equal(2, command.Units.Count);
        Assert.DoesNotContain(command.Units, u => u.AllocationId == unreturned.AllocationId);

        var units = new[]
        {
            new AppliedReturnedUnitPayload(normal.AllocationId, normal.AssetId, "RELEASED", Now, null),
            new AppliedReturnedUnitPayload(damaged.AllocationId, damaged.AssetId, "MAINTENANCE", Now, "block-ref")
        };
        var envelope = Envelope(order, item, record.Value.Id, command.OperationKey, units);
        var (handler, save, _) = Handler(order);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);

        Assert.True((await handler.ApplyStagedAsync(json)).IsSuccess);
        Assert.True((await handler.ApplyStagedAsync(json)).IsSuccess);
        Assert.Equal(0, save());
        Assert.NotNull(normal.AllocationClosedAt);
        Assert.NotNull(damaged.AllocationClosedAt);
        Assert.Equal("block-ref", damaged.InventoryDispositionRef);
        Assert.Null(unreturned.AllocationClosedAt);
        Assert.Null(item.InventoryClosureConfirmedAt);
    }

    [Fact]
    public async Task DamagedResultWithoutProtection_DoesNotCloseAllocation()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var record = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "scratch", [])], Now, item.SupplierId);
        Assert.True(record.IsSuccess);
        var command = ReturnService.ScheduleKafkaInventoryReturn(order, item, record.Value, Now);
        var envelope = Envelope(order, item, record.Value.Id, command.OperationKey,
            [new AppliedReturnedUnitPayload(unit.AllocationId, unit.AssetId, "AVAILABLE", Now, null)]);
        var (handler, save, _) = Handler(order);

        Assert.True((await handler.ApplyStagedAsync(JsonSerializer.Serialize(envelope, JsonOptions))).IsFailure);
        Assert.Equal(0, save());
        Assert.Null(unit.AllocationClosedAt);
        Assert.Null(unit.InventoryDispositionRef);
    }

    [Fact]
    public async Task FullReturn_EmitsRentalItemReturnedWhenInventoryClosureIsConfirmed()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var record = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Normal, null, [])], Now, item.SupplierId);
        Assert.True(record.IsSuccess);
        var command = ReturnService.ScheduleKafkaInventoryReturn(order, item, record.Value, Now);
        var envelope = Envelope(order, item, record.Value.Id, command.OperationKey,
            [new AppliedReturnedUnitPayload(unit.AllocationId, unit.AssetId, "RELEASED", Now, null)]);
        var (handler, _, events) = Handler(order);

        Assert.True((await handler.ApplyStagedAsync(JsonSerializer.Serialize(envelope, JsonOptions))).IsSuccess);
        Assert.NotNull(item.InventoryClosureConfirmedAt);
        Assert.Single(events.ReturnedEvents);
        Assert.Equal(item.ReturnsCompletedAt, events.ReturnedEvents[0].ReturnsCompletedAt);

        Assert.True((await handler.ApplyStagedAsync(JsonSerializer.Serialize(envelope, JsonOptions))).IsSuccess);
        Assert.Single(events.ReturnedEvents);
    }

    private static KafkaMessageEnvelope<ReturnedUnitsAppliedPayload> Envelope(RentalOrder order,
        RentalItem item, Guid returnRecordId, string key, IReadOnlyList<AppliedReturnedUnitPayload> units) =>
        new(Guid.NewGuid(), KafkaContractSpec.EventTypes.ReturnedUnitsApplied, 1,
            KafkaContractSpec.Producers.InventoryService, order.Id.ToString("D"), Now,
            returnRecordId.ToString("D"), null,
            new ReturnedUnitsAppliedPayload(order.Id, item.Id, item.AllocationGroupId,
                returnRecordId, key, "return-ref", Now, units));

    private static (InventoryReturnResultHandler Handler, Func<int> Saves, FakeRentalEventProducer Events) Handler(RentalOrder order)
    {
        var repository = new Mock<IRentalOrderRepository>();
        repository.Setup(r => r.GetByIdWithDetailsAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        repository.Setup(r => r.UpdateAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var events = new FakeRentalEventProducer();
        return (new InventoryReturnResultHandler(repository.Object, new FakeTimeProvider(Now), events), () => 0, events);
    }
}
