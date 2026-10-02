using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử toàn diện cho State Machine của AssetAllocation trong target domain.
/// Kiểm tra tất cả các transitions: CreateHold, CreateManual, Allocate, ExtendHold, ReleaseHold, ExpireHold, Cancel, Release.
/// </summary>
public sealed class AllocationStateMachineTests
{
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly DateTimeOffset _baseNow = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private readonly RentalPeriod _validPeriod = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

    [Fact]
    public void CreateHold_ValidData_SetsHeldStatusAndTransition()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(24);

        var result = AssetAllocation.CreateHold(
            id: Guid.NewGuid(),
            assetId: _assetId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow,
            sourceSystem: "rental_service",
            externalReference: "EXT-001");

        Assert.True(result.IsSuccess);
        var alloc = result.Value;
        Assert.Equal(AllocationStatus.Held, alloc.Status);
        Assert.Equal(holdExpiresAt, alloc.HoldExpiresAt);
        Assert.Single(alloc.Transitions);

        var transition = alloc.Transitions.First();
        Assert.Equal(AllocationOperationType.CreateHold, transition.OperationType);
        Assert.Null(transition.FromStatus);
        Assert.Equal(AllocationStatus.Held, transition.ToStatus);
        Assert.Equal(1, transition.Sequence);
    }

    [Fact]
    public void CreateHold_HoldExpiresInPast_Fails()
    {
        DateTimeOffset holdExpiresAt = _baseNow.AddHours(-1);

        var result = AssetAllocation.CreateHold(
            id: Guid.NewGuid(),
            assetId: _assetId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow,
            sourceSystem: "rental_service",
            externalReference: "EXT-002");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_IN_PAST", result.Error.Code);
    }

    [Fact]
    public void CreateHold_HoldExpiresAfterStartDate_Fails()
    {
        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(_validPeriod.StartDate);
        DateTimeOffset holdExpiresAt = startOfDayUtc.AddHours(1);

        var result = AssetAllocation.CreateHold(
            id: Guid.NewGuid(),
            assetId: _assetId,
            period: _validPeriod,
            holdExpiresAt: holdExpiresAt,
            now: _baseNow,
            sourceSystem: "rental_service",
            externalReference: "EXT-003");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_AFTER_START", result.Error.Code);
    }

    [Fact]
    public void CreateManual_ValidData_SetsAllocatedStatus()
    {
        var result = AssetAllocation.CreateManual(
            id: Guid.NewGuid(),
            assetId: _assetId,
            period: _validPeriod,
            now: _baseNow,
            operationalReference: "OPER-001");

        Assert.True(result.IsSuccess);
        var alloc = result.Value;
        Assert.Equal(AllocationStatus.Allocated, alloc.Status);
        Assert.Null(alloc.HoldExpiresAt);
        Assert.Equal("inventory.manual", alloc.SourceSystem);
        Assert.Equal("OPER-001", alloc.ExternalReference);
        Assert.Single(alloc.Transitions);

        var transition = alloc.Transitions.First();
        Assert.Equal(AllocationOperationType.CreateManual, transition.OperationType);
        Assert.Null(transition.FromStatus);
        Assert.Equal(AllocationStatus.Allocated, transition.ToStatus);
    }

    [Fact]
    public void Allocate_ValidHeld_TransitionsToAllocated()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-10").Value;

        var result = alloc.Allocate(_baseNow.AddHours(1), "rental_service");

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Allocated, alloc.Status);
        Assert.Equal(2, alloc.Transitions.Count);
        Assert.Equal(AllocationOperationType.Allocate, alloc.Transitions.Last().OperationType);
    }

    [Fact]
    public void Allocate_ExpiredHeld_Fails()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-11").Value;

        var result = alloc.Allocate(_baseNow.AddHours(3), "rental_service");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRED", result.Error.Code);
        Assert.Equal(AllocationStatus.Held, alloc.Status);
    }

    [Fact]
    public void ExtendHold_ValidExtension_ExtendsExpiry()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-12").Value;

        DateTimeOffset newExpiry = _baseNow.AddHours(4);
        var result = alloc.ExtendHold(newExpiry, _baseNow.AddHours(1), "rental_service");

        Assert.True(result.IsSuccess);
        Assert.Equal(newExpiry, alloc.HoldExpiresAt);
        Assert.Equal(2, alloc.Transitions.Count);
    }

    [Fact]
    public void ExtendHold_ExpiredHold_Fails()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-13").Value;

        DateTimeOffset newExpiry = _baseNow.AddHours(4);
        var result = alloc.ExtendHold(newExpiry, _baseNow.AddHours(3), "rental_service");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRED", result.Error.Code);
    }

    [Fact]
    public void ReleaseHold_ValidHeld_TransitionsToReleased()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-14").Value;

        var result = alloc.ReleaseHold("Khách hủy đơn thuê", _baseNow.AddHours(1), "rental_service");

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Released, alloc.Status);
    }

    [Fact]
    public void ExpireHold_ExpiredHeld_TransitionsToReleased()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-15").Value;

        var result = alloc.ExpireHold(_baseNow.AddHours(3), "system");

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Released, alloc.Status);
    }

    [Fact]
    public void ExpireHold_NotYetExpired_Fails()
    {
        var alloc = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow.AddHours(2), _baseNow, "rental", "EXT-16").Value;

        var result = alloc.ExpireHold(_baseNow.AddHours(1), "system");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_NOT_YET_EXPIRED", result.Error.Code);
    }

    [Fact]
    public void Cancel_Allocated_TransitionsToCancelled()
    {
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow).Value;

        var result = alloc.Cancel("Đơn hàng bị hủy", _baseNow.AddHours(1), "operator");

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Cancelled, alloc.Status);
    }

    [Fact]
    public void Release_Allocated_TransitionsToReleased()
    {
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow).Value;

        var result = alloc.Release("Hoàn tất thời gian thuê", _baseNow.AddHours(1), "operator");

        Assert.True(result.IsSuccess);
        Assert.Equal(AllocationStatus.Released, alloc.Status);
    }

    [Fact]
    public void TerminalState_RejectsFurtherTransitions()
    {
        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), _assetId, _validPeriod, _baseNow).Value;
        Assert.True(alloc.Cancel("Hủy", _baseNow.AddHours(1), "operator").IsSuccess);

        Assert.True(alloc.IsTerminalState());
        var secondCancel = alloc.Cancel("Hủy lại", _baseNow.AddHours(2), "operator");
        Assert.True(secondCancel.IsFailure);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", secondCancel.Error.Code);

        var release = alloc.Release("Nhả", _baseNow.AddHours(2), "operator");
        Assert.True(release.IsFailure);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", release.Error.Code);
    }
}
