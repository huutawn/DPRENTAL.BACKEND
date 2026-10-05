using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Bộ kiểm thử đơn vị cho AssetAvailabilityPolicy trong target domain.
/// Kiểm tra công tắc AcceptingBookings, các khoảng CalendarBlock, Held còn hạn vs hết hạn, và Allocated.
/// </summary>
public sealed class AvailabilityPolicyTests
{
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

    private InventoryAsset CreateActiveAsset(bool acceptingBookings = true)
    {
        var asset = InventoryAsset.Create(
            _assetId,
            _ownerUserId,
            _equipmentTypeId,
            Guid.NewGuid(),
            "Sony FX3",
            1,
            "{\"serial\":\"123\"}",
            _now).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", _now);
        }

        return asset;
    }

    [Fact]
    public void CheckAvailability_WhenNotAcceptingBookings_Fails()
    {
        var asset = CreateActiveAsset(acceptingBookings: false);
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [],
            existingBlocks: []);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_ACCEPTING_BOOKINGS", result.Error.Code);
    }

    [Fact]
    public void CheckAvailability_ExpiredHold_DoesNotOccupyCalendar()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        DateTimeOffset holdExpiredTime = _now.AddHours(-2);
        var expiredHold = AssetAllocation.CreateHold(
            Guid.NewGuid(),
            _assetId,
            candidate,
            holdExpiredTime,
            _now.AddHours(-10),
            "rental",
            "EXT-01").Value;

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [expiredHold],
            existingBlocks: []);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void CheckAvailability_ActiveHold_OccupiesCalendarAndConflicts()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        DateTimeOffset holdExpiresAt = _now.AddHours(5);
        var activeHold = AssetAllocation.CreateHold(
            Guid.NewGuid(),
            _assetId,
            candidate,
            holdExpiresAt,
            _now,
            "rental",
            "EXT-02").Value;

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [activeHold],
            existingBlocks: []);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public void CheckAvailability_Allocated_OccupiesCalendarAndConflicts()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var allocated = AssetAllocation.CreateManual(
            Guid.NewGuid(),
            _assetId,
            candidate,
            _now).Value;

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [allocated],
            existingBlocks: []);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public void CheckAvailability_ReleasedOrCancelled_DoesNotOccupyCalendar()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var alloc = AssetAllocation.CreateManual(
            Guid.NewGuid(),
            _assetId,
            candidate,
            _now).Value;
        alloc.Cancel("Hủy", _now.AddHours(1), "operator");

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [alloc],
            existingBlocks: []);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void CheckAvailability_CalendarBlock_OccupiesCalendarAndConflicts()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var block = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            new DateOnly(2026, 10, 16),
            new DateOnly(2026, 10, 17),
            CalendarBlockReason.Maintenance,
            "tech",
            _now,
            new DateOnly(2026, 10, 10)).Value;

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [],
            existingBlocks: [block]);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_BLOCKED", result.Error.Code);
    }

    [Fact]
    public void CheckAvailability_SelfExcludeAllocationId_IgnoresSelf()
    {
        var asset = CreateActiveAsset();
        var candidate = new RentalPeriod(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 18));

        var allocated = AssetAllocation.CreateManual(
            Guid.NewGuid(),
            _assetId,
            candidate,
            _now).Value;

        var result = AssetAvailabilityPolicy.CheckAvailability(
            asset,
            candidate,
            _now,
            existingAllocations: [allocated],
            existingBlocks: [],
            excludeAllocationId: allocated.Id);

        Assert.True(result.IsSuccess);
    }
}
