using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

/// <summary>
/// Unit tests cho CalendarBlock domain entity và AssetAvailabilityPolicy theo DPR-149.
/// </summary>
public sealed class CalendarBlockDomainTests
{
    private readonly Guid _assetId = Guid.NewGuid();
    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _equipmentTypeId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly DateOnly _today = new(2026, 10, 2);

    private InventoryAsset CreateAsset(bool acceptingBookings = true)
    {
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            _ownerUserId,
            _equipmentTypeId,
            Guid.NewGuid(),
            "Sony FX3",
            null,
            "Sony FX3",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            _now).Value;

        var asset = InventoryAsset.Create(_assetId, listing.Id, "SN-CAL-1", _now).Value;
        typeof(InventoryAsset).GetProperty(nameof(InventoryAsset.Listing))?.SetValue(asset, listing);

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", _now);
        }

        return asset;
    }

    [Fact]
    public void Create_StartDateToday_Succeeds()
    {
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: _today.AddDays(2),
            CalendarBlockReason.Maintenance,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsSuccess);
        Assert.Equal(_today, result.Value.StartDate);
        Assert.Equal(_today.AddDays(2), result.Value.EndDate);
        Assert.Equal(CalendarBlockReason.Maintenance, result.Value.Reason);
    }

    [Fact]
    public void Create_StartDateInPast_Fails()
    {
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today.AddDays(-1),
            endDate: _today.AddDays(2),
            CalendarBlockReason.Maintenance,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsFailure);
        Assert.Equal("START_DATE_IN_PAST", result.Error.Code);
    }

    [Fact]
    public void Create_EndDateAtExactHorizon_Succeeds()
    {
        DateOnly horizon = _today.AddMonths(6);
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: horizon,
            CalendarBlockReason.OwnerReserved,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsSuccess);
        Assert.Equal(horizon, result.Value.EndDate);
    }

    [Fact]
    public void Create_EndDateOneDayPastHorizon_Fails()
    {
        DateOnly pastHorizon = _today.AddMonths(6).AddDays(1);
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: pastHorizon,
            CalendarBlockReason.Inspection,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsFailure);
        Assert.Equal("EXCEEDS_BLOCK_HORIZON", result.Error.Code);
    }

    [Fact]
    public void Create_ReversedDates_Fails()
    {
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today.AddDays(5),
            endDate: _today.AddDays(2),
            CalendarBlockReason.Other,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_DATE_ORDER", result.Error.Code);
    }

    [Fact]
    public void Create_InvalidReason_Fails()
    {
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: _today.AddDays(1),
            reason: (CalendarBlockReason)999,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BLOCK_REASON", result.Error.Code);
    }

    [Fact]
    public void Create_NoteExceeds500Chars_Fails()
    {
        string longNote = new('a', 501);
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: _today.AddDays(1),
            reason: CalendarBlockReason.Maintenance,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today,
            notes: longNote);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_BLOCK_NOTES", result.Error.Code);
    }

    [Fact]
    public void Create_Note500Chars_TrimsAndSucceeds()
    {
        string validNote = "   " + new string('b', 490) + "   ";
        var result = CalendarBlock.Create(
            Guid.NewGuid(),
            _assetId,
            startDate: _today,
            endDate: _today.AddDays(1),
            reason: CalendarBlockReason.Maintenance,
            createdBy: _ownerUserId.ToString("D"),
            now: _now,
            today: _today,
            notes: validNote);

        Assert.True(result.IsSuccess);
        Assert.Equal(490, result.Value.Notes?.Length);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "11111111-1111-1111-1111-111111111111", "user", "INVALID_BLOCK_ID")]
    [InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000", "user", "INVALID_ASSET_ID")]
    [InlineData("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "", "CREATED_BY_REQUIRED")]
    [InlineData("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "   ", "CREATED_BY_REQUIRED")]
    public void Create_InvalidIdentifiers_Fails(string blockIdStr, string assetIdStr, string createdBy, string expectedCode)
    {
        var result = CalendarBlock.Create(
            Guid.Parse(blockIdStr),
            Guid.Parse(assetIdStr),
            _today,
            _today.AddDays(1),
            CalendarBlockReason.Maintenance,
            createdBy,
            _now,
            _today);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public void Overlap_SingleDayInterval_Conflicts()
    {
        var block = CalendarBlock.Create(
            Guid.NewGuid(), _assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10),
            CalendarBlockReason.Maintenance, "user", _now, _today).Value;

        var result = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10),
            _now, existingAllocations: [], existingBlocks: [block]);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_BLOCKED", result.Error.Code);
    }

    [Fact]
    public void Overlap_BoundaryMatching_Conflicts()
    {
        // Existing [10-10, 10-12], new starts on 10-12 [10-12, 10-15]
        var block = CalendarBlock.Create(
            Guid.NewGuid(), _assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12),
            CalendarBlockReason.Maintenance, "user", _now, _today).Value;

        var result = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15),
            _now, existingAllocations: [], existingBlocks: [block]);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_BLOCKED", result.Error.Code);
    }

    [Fact]
    public void Overlap_AdjacentNonOverlapping_Succeeds()
    {
        // Existing [10-10, 10-10], candidate [10-11, 10-12]
        var block = CalendarBlock.Create(
            Guid.NewGuid(), _assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10),
            CalendarBlockReason.Maintenance, "user", _now, _today).Value;

        var result = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12),
            _now, existingAllocations: [], existingBlocks: [block]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Overlap_Containment_Conflicts()
    {
        // Existing [10-10, 10-20], candidate [10-12, 10-15] inside
        var block = CalendarBlock.Create(
            Guid.NewGuid(), _assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 20),
            CalendarBlockReason.Maintenance, "user", _now, _today).Value;

        var result = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 15),
            _now, existingAllocations: [], existingBlocks: [block]);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_BLOCKED", result.Error.Code);
    }

    [Fact]
    public void Overlap_LeftAndRightOverlap_Conflicts()
    {
        var block = CalendarBlock.Create(
            Guid.NewGuid(), _assetId, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15),
            CalendarBlockReason.Maintenance, "user", _now, _today).Value;

        // Left overlap: [10-08, 10-11]
        var leftResult = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 11),
            _now, existingAllocations: [], existingBlocks: [block]);
        Assert.True(leftResult.IsFailure);

        // Right overlap: [10-14, 10-18]
        var rightResult = AssetAvailabilityPolicy.CheckCalendarOverlap(
            new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 18),
            _now, existingAllocations: [], existingBlocks: [block]);
        Assert.True(rightResult.IsFailure);
    }

    [Fact]
    public void OccupiesCalendar_HeldStatus_DistinguishesExpiry()
    {
        var period = new RentalPeriod(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15));

        // 1. Held còn hạn: HoldExpiresAt > now -> Chiếm lịch
        var activeHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, period, _now.AddHours(2), _now, "src", "REF-ACT").Value;
        Assert.True(AssetAvailabilityPolicy.OccupiesCalendar(activeHold, _now));

        // 2. Held đã quá hạn: HoldExpiresAt < now -> Không chiếm lịch
        var expiredHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, period, _now.AddHours(-1), _now.AddHours(-5), "src", "REF-EXP").Value;
        Assert.False(AssetAvailabilityPolicy.OccupiesCalendar(expiredHold, _now));

        // 3. Held expiry đúng bằng now: HoldExpiresAt == now -> Không chiếm lịch
        var exactExpiryHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), _assetId, period, _now, _now.AddHours(-1), "src", "REF-EXACT").Value;
        Assert.False(AssetAvailabilityPolicy.OccupiesCalendar(exactExpiryHold, _now));
    }

    [Fact]
    public void OccupiesCalendar_Allocated_OccupiesCalendar()
    {
        var period = new RentalPeriod(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15));
        var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), _assetId, period, _now, "REF-ALLOC").Value;

        Assert.Equal(AllocationStatus.Allocated, alloc.Status);
        Assert.True(AssetAvailabilityPolicy.OccupiesCalendar(alloc, _now));

        // Overlap with allocated conflicts
        var result = AssetAvailabilityPolicy.CheckCalendarOverlap(
            period.StartDate, period.EndDate, _now, existingAllocations: [alloc], existingBlocks: []);
        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", result.Error.Code);
    }

    [Fact]
    public void OccupiesCalendar_ReleasedAndCancelled_DoNotOccupy()
    {
        var period = new RentalPeriod(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 15));

        // 1. Cancelled: Allocated -> Cancelled
        var manualAlloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), _assetId, period, _now, "REF-CANCEL").Value;
        var cancelResult = manualAlloc.Cancel("Customer cancelled", _now.AddMinutes(10), "operator");
        Assert.True(cancelResult.IsSuccess);
        Assert.Equal(AllocationStatus.Cancelled, manualAlloc.Status);
        Assert.False(AssetAvailabilityPolicy.OccupiesCalendar(manualAlloc, _now));

        var cancelOverlap = AssetAvailabilityPolicy.CheckCalendarOverlap(
            period.StartDate, period.EndDate, _now, existingAllocations: [manualAlloc], existingBlocks: []);
        Assert.True(cancelOverlap.IsSuccess);

        // 2. Released: Allocated -> Released
        var releaseAlloc = AssetAllocation.CreateManual(
            Guid.NewGuid(), _assetId, period, _now, "REF-RELEASE").Value;
        var releaseResult = releaseAlloc.Release("Returned", _now.AddMinutes(15), "operator");
        Assert.True(releaseResult.IsSuccess);
        Assert.Equal(AllocationStatus.Released, releaseAlloc.Status);
        Assert.False(AssetAvailabilityPolicy.OccupiesCalendar(releaseAlloc, _now));

        var releaseOverlap = AssetAvailabilityPolicy.CheckCalendarOverlap(
            period.StartDate, period.EndDate, _now, existingAllocations: [releaseAlloc], existingBlocks: []);
        Assert.True(releaseOverlap.IsSuccess);
    }

    [Fact]
    public void CheckCalendarOverlap_WhenNotAcceptingBookings_StillSucceeds()
    {
        var asset = CreateAsset(acceptingBookings: false);
        Assert.False(asset.CanAcceptBookings());

        // CheckCalendarOverlap does not check AcceptingBookings
        var overlapResult = AssetAvailabilityPolicy.CheckCalendarOverlap(
            _today, _today.AddDays(3), _now, existingAllocations: [], existingBlocks: []);
        Assert.True(overlapResult.IsSuccess);

        // While CheckAvailability still rejects
        var period = new RentalPeriod(_today, _today.AddDays(3));
        var availResult = AssetAvailabilityPolicy.CheckAvailability(
            asset, period, _now, existingAllocations: [], existingBlocks: []);
        Assert.True(availResult.IsFailure);
        Assert.Equal("ASSET_NOT_ACCEPTING_BOOKINGS", availResult.Error.Code);
    }
}
