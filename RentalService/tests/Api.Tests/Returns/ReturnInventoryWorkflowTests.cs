using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Returns;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Returns;

public sealed class ReturnInventoryWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalReturn_CreatesOneClosureReceipt_AndWaitsForSettlement()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var result = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Normal, null, [])], Now, item.SupplierId);
        Assert.True(result.IsSuccess);
        ReturnService.ScheduleInventoryReturn(order, item, result.Value, Now);

        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.Equal(0m, item.FinalDeductionAmount);
        Assert.Null(item.InventoryClosureConfirmedAt);
        Assert.Null(item.SettlementRef);
        var receipt = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.MarkReturnedAction);
        var request = JsonSerializer.Deserialize<MarkAssetsReturnedRequest>(receipt.PayloadJson!);
        Assert.NotNull(request);
        Assert.Equal(unit.AllocationId, Assert.Single(request.Units).AllocationId);
        Assert.Equal(UnitCondition.Normal, request.Units[0].Condition);
    }

    [Fact]
    public async Task MixedReturn_NormalClosesSeparately_DamagedMustBlockBeforeClosure()
    {
        var order = ReturnTests.NewOrder();
        var item = order.Items.Single();
        var normal = item.UnitAssignments.First();
        var damaged = item.UnitAssignments.Last();
        var result = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(normal.Id, UnitCondition.Normal, null, []),
             new ReturnUnitData(damaged.Id, UnitCondition.Damaged, "scratch", [])], Now, item.SupplierId);
        Assert.True(result.IsSuccess);
        ReturnService.ScheduleInventoryReturn(order, item, result.Value, Now);

        var block = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.BlockDamagedAction);
        var closes = order.OperationReceipts.Where(r => r.Action == RentalItem.MarkReturnedAction).ToArray();
        Assert.Equal(2, closes.Length);
        var normalClose = closes.Single(r => r.PrerequisiteOperationId is null);
        var damageClose = closes.Single(r => r.PrerequisiteOperationId == block.OperationId);
        var inventory = new Mock<IInventoryRentalClient>();
        inventory.Setup(x => x.MarkAssetsReturnedAsync(item.AllocationGroupId,
                It.IsAny<MarkAssetsReturnedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, MarkAssetsReturnedRequest request, CancellationToken _) =>
                Result.Success(new MarkAssetsReturnedResponse("return-ref", request.OperationKey, group,
                    Now, false, request.Units.Select(u =>
                    {
                        var assignment = item.UnitAssignments.Single(a => a.AllocationId == u.AllocationId);
                        return new ReturnedUnitItemResponse(u.AllocationId, assignment.AssetId, "CLOSED", Now,
                            u.Condition == UnitCondition.Damaged ? "damage-ref" : null);
                    }).ToArray())));
        inventory.Setup(x => x.BlockDamagedAssetsAsync(item.AllocationGroupId,
                It.IsAny<BlockDamagedAssetsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, BlockDamagedAssetsRequest request, CancellationToken _) =>
                Result.Success(new BlockDamagedAssetsResponse("block-ref", request.OperationKey, group,
                    Now, false, [new BlockedDamagedUnitResponse(damaged.AllocationId, damaged.AssetId,
                        "BLOCKED", "damage-ref")])));

        var executor = Executor(order, inventory.Object);
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(normalClose))).IsSuccess);
        Assert.NotNull(normal.AllocationClosedAt);
        Assert.Null(damaged.AllocationClosedAt);
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(damageClose))).IsSuccess);
        inventory.Verify(x => x.MarkAssetsReturnedAsync(item.AllocationGroupId,
            It.Is<MarkAssetsReturnedRequest>(r => r.Units.Any(u => u.AllocationId == damaged.AllocationId)),
            It.IsAny<CancellationToken>()), Times.Never);

        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(block))).IsSuccess);
        Assert.Equal("damage-ref", damaged.InventoryDispositionRef);
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(damageClose))).IsSuccess);
        Assert.NotNull(damaged.AllocationClosedAt);
        Assert.NotNull(item.InventoryClosureConfirmedAt);
        Assert.Equal(RentalItemStatus.PendingSettlement, item.Status);
        Assert.Null(item.SettlementRef);
    }

    [Fact]
    public async Task DamagedUnit_RejectsUnprotectedInventoryResponse()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var returned = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "damage", [])], Now, item.SupplierId);
        Assert.True(returned.IsSuccess);
        ReturnService.ScheduleInventoryReturn(order, item, returned.Value, Now);
        var block = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.BlockDamagedAction);
        var close = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.MarkReturnedAction);
        var inventory = new Mock<IInventoryRentalClient>();
        inventory.Setup(x => x.BlockDamagedAssetsAsync(item.AllocationGroupId,
                It.IsAny<BlockDamagedAssetsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, BlockDamagedAssetsRequest request, CancellationToken _) =>
                Result.Success(new BlockDamagedAssetsResponse("receipt", request.OperationKey, group,
                    Now, false, [new BlockedDamagedUnitResponse(unit.AllocationId, unit.AssetId,
                        "AVAILABLE", "damage-ref")])));

        var executor = Executor(order, inventory.Object);
        var result = await executor.ExecuteScheduledOperationAsync(Describe(block));
        Assert.True(result.IsFailure);
        Assert.Null(unit.InventoryDispositionRef);
        Assert.Null(unit.AllocationClosedAt);
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(close))).IsSuccess);
        inventory.Verify(x => x.MarkAssetsReturnedAsync(It.IsAny<string>(),
            It.IsAny<MarkAssetsReturnedRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NormalClosure_TransientFailureRetriesWithSameInventoryKey()
    {
        var order = ReturnTests.NewOrder(quantity: 1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        var returned = item.RecordAssetReturns(item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Normal, null, [])], Now, item.SupplierId);
        Assert.True(returned.IsSuccess);
        ReturnService.ScheduleInventoryReturn(order, item, returned.Value, Now);
        var receipt = Assert.Single(order.OperationReceipts, r => r.Action == RentalItem.MarkReturnedAction);
        var keys = new List<string>();
        var inventory = new Mock<IInventoryRentalClient>();
        inventory.Setup(x => x.MarkAssetsReturnedAsync(item.AllocationGroupId,
                It.IsAny<MarkAssetsReturnedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, MarkAssetsReturnedRequest request, CancellationToken _) =>
            {
                keys.Add(request.OperationKey);
                return keys.Count == 1
                    ? Result.Failure<MarkAssetsReturnedResponse>(Error.ServiceUnavailable("INVENTORY_TIMEOUT", "timeout"))
                    : Result.Success(new MarkAssetsReturnedResponse("return-ref", request.OperationKey, group,
                        Now, true, [new ReturnedUnitItemResponse(unit.AllocationId, unit.AssetId,
                            "CLOSED", Now, null)]));
            });
        var clock = new FakeTimeProvider(Now);
        var executor = Executor(order, inventory.Object, clock);
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(receipt))).IsFailure);
        Assert.Null(unit.AllocationClosedAt);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await executor.ExecuteScheduledOperationAsync(Describe(receipt))).IsSuccess);
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        Assert.NotNull(unit.AllocationClosedAt);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);
    }

    private static OperationReceiptDescriptor Describe(RentalOperationReceipt r) => new(
        r.Id, r.RentalOrderId, r.OperationId, r.Action, r.TargetItemId, r.Status,
        r.RetryCount, r.NextRetryAt, r.CreatedAt, r.PrerequisiteOperationId, r.IsOutcomeUncertain);

    private static RentalOperationExecutor Executor(RentalOrder order, IInventoryRentalClient inventory,
        FakeTimeProvider? clock = null)
    {
        var repo = new Mock<IRentalOrderRepository>();
        repo.Setup(x => x.GetByIdWithDetailsAsync(order.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(order);
        repo.Setup(x => x.UpdateAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var locks = new Mock<IOrderLockManager>();
        locks.Setup(x => x.AcquireOrderLockAsync(order.Id, It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NoopLock());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return new RentalOperationExecutor(repo.Object, Mock.Of<IOperationReceiptRepository>(),
            Mock.Of<IOutboxRepository>(), locks.Object, inventory, Mock.Of<IPaymentRentalClient>(),
            uow.Object, new ConfigurationBuilder().Build(), NullLogger<RentalOperationExecutor>.Instance,
            clock ?? new FakeTimeProvider(Now));
    }

    private sealed class NoopLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
