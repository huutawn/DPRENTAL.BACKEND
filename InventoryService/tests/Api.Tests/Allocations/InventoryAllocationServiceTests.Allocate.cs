using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Allocations;

public sealed class InventoryAllocationServiceAllocateTests
{
    private static readonly DateTimeOffset FixedNow = AllocationTestConstants.FixedNow;
    private static readonly DateOnly FixedToday = AllocationTestConstants.FixedToday;

    private static (InventoryAllocationService Service, FakeAssetRepository Assets, FakeAllocationRepository Allocs, FakeCalendarBlockRepository Blocks, FakeWriteTransaction Tx, TestTimeProvider Clock) CreateService(DateTimeOffset? now = null)
    {
        var clockNow = now ?? FixedNow;
        var assets = new FakeAssetRepository();
        var allocs = new FakeAllocationRepository();
        var blocks = new FakeCalendarBlockRepository();
        var tx = new FakeWriteTransaction(assets, allocs, blocks);
        var clock = new TestTimeProvider(clockNow);
        var service = new InventoryAllocationService(allocs, tx, clock);
        return (service, assets, allocs, blocks, tx, clock);
    }

    [Fact]
    public async Task Allocate_Authorization_Checks()
    {
        var (service, _, _, _, _, _) = CreateService();
        var allocId = Guid.NewGuid();

        // 1. Unauthenticated -> 401
        var res1 = await service.AllocateAsync(allocId, new AllocateRequest("op-key"), new TestCaller { IsAuthenticated = false });
        Assert.Equal("UNAUTHORIZED", res1.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, res1.Error.Type);

        // 2. Human -> 403
        var res2 = await service.AllocateAsync(allocId, new AllocateRequest("op-key"), new TestCaller { IsAuthenticated = true, IsService = false, UserId = Guid.NewGuid() });
        Assert.Equal("FORBIDDEN", res2.Error.Code);
        Assert.Equal(ErrorType.Forbidden, res2.Error.Type);

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Allocate_Validation_InvalidOperationKey_Returns400(string invalidKey)
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var allocId = Guid.NewGuid();

        var result = await service.AllocateAsync(allocId, new AllocateRequest(invalidKey), caller);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_OPERATION_KEY", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task Allocate_Validation_OperationKeyTooLong_Returns400()
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var allocId = Guid.NewGuid();

        string tooLongKey = new('x', 101);
        var result = await service.AllocateAsync(allocId, new AllocateRequest(tooLongKey), caller);
        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_OPERATION_KEY", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Fact]
    public async Task Allocate_NotFound_Returns404()
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller();

        var result = await service.AllocateAsync(Guid.NewGuid(), new AllocateRequest("op-key"), caller);
        Assert.True(result.IsFailure);
        Assert.Equal("ALLOCATION_NOT_FOUND", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task Allocate_ValidHeld_TransitionsToAllocated_WithoutOutboxOrBookingCheck()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId, acceptingBookings: false)); // Even with bookings disabled!

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(2), FixedNow, "sys", "ref-alloc").Value;
        allocs.Add(hold);

        var result = await service.AllocateAsync(hold.Id, new AllocateRequest("op-100"), caller);

        Assert.True(result.IsSuccess);
        Assert.Equal("Allocated", result.Value.Status);
        Assert.Equal(AllocationStatus.Allocated, hold.Status);
        Assert.Equal(2, hold.Transitions.Count);
        Assert.Equal("op-100", hold.Transitions.Last().OperationKey);
        Assert.Empty(tx.StagedOutbox); // Allocate does NOT emit outbox
    }

    [Fact]
    public async Task Allocate_Replay_SameOperationKey_ReturnsSuccessWithoutNewTransition()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(2), FixedNow, "sys", "ref-alloc").Value;
        allocs.Add(hold);

        // First call
        var res1 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-200"), caller);
        Assert.True(res1.IsSuccess);
        Assert.Equal(2, hold.Transitions.Count);

        // Second call (replay)
        var res2 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-200"), caller);
        Assert.True(res2.IsSuccess);
        Assert.Equal(2, hold.Transitions.Count); // No new transition appended
    }

    [Fact]
    public async Task Allocate_Replay_SameOperationKey_AfterExpiry_StillSucceedsWithoutNewTransition()
    {
        var (service, assets, allocs, _, _, clock) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(2), FixedNow, "sys", "ref-alloc-replay-exp").Value;
        allocs.Add(hold);

        // 1. Allocate thành công lúc hold còn hạn
        var res1 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-replay-exp"), caller);
        Assert.True(res1.IsSuccess);

        // 2. Thời gian trôi qua quá hạn ban đầu
        clock.Advance(TimeSpan.FromHours(5));

        // 3. Replay với cùng operation key vẫn phải thành công mà không báo HOLD_EXPIRED hay thêm transition
        var res2 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-replay-exp"), caller);
        Assert.True(res2.IsSuccess);
        Assert.Equal("Allocated", res2.Value.Status);
        Assert.Equal(2, hold.Transitions.Count);
    }

    [Fact]
    public async Task Allocate_ExpiredHold_WithNewKey_FailsWithConflictDirectlyFromApplication()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(-1), FixedNow.AddHours(-3), "sys", "ref-exp").Value;
        allocs.Add(hold);

        var result = await service.AllocateAsync(hold.Id, new AllocateRequest("op-new"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRED", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type); // Application maps to Conflict directly!
    }

    [Fact]
    public async Task Allocate_AlreadyAllocated_WithNewKey_FailsWithConflictDirectlyFromApplication()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(2), FixedNow, "sys", "ref-already-alloc").Value;
        allocs.Add(hold);

        // Allocate lần 1 với key-1
        var res1 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-key-1"), caller);
        Assert.True(res1.IsSuccess);

        // Allocate lần 2 với key mới khác
        var res2 = await service.AllocateAsync(hold.Id, new AllocateRequest("op-key-2"), caller);
        Assert.True(res2.IsFailure);
        Assert.Equal("INVALID_STATUS_TRANSITION", res2.Error.Code);
        Assert.Equal(ErrorType.Conflict, res2.Error.Type); // Application maps to Conflict directly!
    }

    [Fact]
    public async Task Allocate_TerminalState_Released_ReturnsConflict()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var hold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, FixedNow.AddHours(2), FixedNow, "sys", "ref-terminal-rel").Value;
        Assert.True(hold.ReleaseHold("Cancelled by renter", FixedNow.AddHours(1), "system").IsSuccess);
        allocs.Add(hold);

        var result = await service.AllocateAsync(hold.Id, new AllocateRequest("op-terminal"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task Allocate_TerminalState_Cancelled_ReturnsConflict()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), assetId, period, FixedNow, "ref-terminal-canc").Value;
        Assert.True(alloc.Cancel("Cancelled by operator", FixedNow.AddHours(1), "operator").IsSuccess);
        allocs.Add(alloc);

        var result = await service.AllocateAsync(alloc.Id, new AllocateRequest("op-canc"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public async Task Allocate_IdempotencyKeyMisuse_UsedInOtherOperation_ReturnsConflict()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();

        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        // Tạo manual allocation với operationKey "KEY-MANUAL"
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), assetId, period, FixedNow, "ref-op-misuse", actor: "operator", operationKey: "KEY-SHARED").Value;
        allocs.Add(alloc);

        // Gọi Allocate với cùng "KEY-SHARED"
        var result = await service.AllocateAsync(alloc.Id, new AllocateRequest("KEY-SHARED"), caller);

        Assert.True(result.IsFailure);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }
}
