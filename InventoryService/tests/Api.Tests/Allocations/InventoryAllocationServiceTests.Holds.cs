using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Allocations;

public sealed class InventoryAllocationServiceHoldTests
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
    public async Task CreateHold_Unauthorized_WhenNotAuthenticated()
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller { IsAuthenticated = false };
        var req = new CreateHoldRequest("sys", "ref", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2));

        var result = await service.CreateHoldAsync(Guid.NewGuid(), req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("UNAUTHORIZED", result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Fact]
    public async Task CreateHold_Forbidden_WhenCallerIsHumanOrLacksScope()
    {
        var (service, _, _, _, _, _) = CreateService();
        var req = new CreateHoldRequest("sys", "ref", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2));

        var humanCaller = new TestCaller { IsAuthenticated = true, IsService = false, UserId = Guid.NewGuid() };
        var res1 = await service.CreateHoldAsync(Guid.NewGuid(), req, humanCaller);
        Assert.True(res1.IsFailure);
        Assert.Equal("FORBIDDEN", res1.Error.Code);
        Assert.Equal(ErrorType.Forbidden, res1.Error.Type);

        var serviceWithoutScope = new TestCaller { IsAuthenticated = true, IsService = true, Scopes = new HashSet<string>() };
        var res2 = await service.CreateHoldAsync(Guid.NewGuid(), req, serviceWithoutScope);
        Assert.True(res2.IsFailure);
        Assert.Equal("FORBIDDEN", res2.Error.Code);
    }

    [Fact]
    public async Task CreateHold_Validation_InvalidInputs()
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller();

        // Missing sourceSystem
        var r1 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("", "ref", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2)), caller);
        Assert.Equal("SOURCE_SYSTEM_REQUIRED", r1.Error.Code);

        // Missing externalReference
        var r2 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("sys", "  ", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2)), caller);
        Assert.Equal("EXTERNAL_REFERENCE_REQUIRED", r2.Error.Code);

        // Default startDate
        var r3 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("sys", "ref", default, FixedToday.AddDays(7), FixedNow.AddHours(2)), caller);
        Assert.Equal("START_DATE_REQUIRED", r3.Error.Code);

        // Default endDate
        var r4 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("sys", "ref", FixedToday.AddDays(5), default, FixedNow.AddHours(2)), caller);
        Assert.Equal("END_DATE_REQUIRED", r4.Error.Code);

        // Default holdExpiresAt
        var r5 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("sys", "ref", FixedToday.AddDays(5), FixedToday.AddDays(7), default), caller);
        Assert.Equal("HOLD_EXPIRES_AT_REQUIRED", r5.Error.Code);

        // startDate > endDate
        var r6 = await service.CreateHoldAsync(Guid.NewGuid(), new CreateHoldRequest("sys", "ref", FixedToday.AddDays(7), FixedToday.AddDays(5), FixedNow.AddHours(2)), caller);
        Assert.Equal("INVALID_DATE_ORDER", r6.Error.Code);
    }

    [Fact]
    public async Task CreateHold_AssetNotFound_Returns404()
    {
        var (service, _, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        var req = new CreateHoldRequest("sys", "ref", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2));

        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_FOUND", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public async Task CreateHold_Precision_ExpiryEqualToNow_Fails()
    {
        var (service, assets, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var req = new CreateHoldRequest("sys", "ref-eq", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow);
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_IN_PAST", result.Error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task CreateHold_Precision_SubMicrosecondTicksAboveNow_NormalizedAndRejected(int ticksAboveNow)
    {
        var (service, assets, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        // Adding sub-microsecond ticks (< 10 ticks = < 1 us) must be truncated by NormalizeUtc to FixedNow,
        // so canonicalHoldExpiresAt becomes <= FixedNow and is rejected before creating an already-expired hold.
        var subMicrosecondExpiry = FixedNow.AddTicks(ticksAboveNow);
        var req = new CreateHoldRequest("sys", $"ref-tick-{ticksAboveNow}", FixedToday.AddDays(5), FixedToday.AddDays(7), subMicrosecondExpiry);
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_IN_PAST", result.Error.Code);
    }

    [Fact]
    public async Task CreateHold_Precision_MicrosecondAboveNow_Accepted()
    {
        var (service, assets, _, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        // 1 microsecond = 10 ticks
        var microsecondAboveNow = FixedNow.AddTicks(10);
        var req = new CreateHoldRequest("sys", "ref-microsecond", FixedToday.AddDays(5), FixedToday.AddDays(7), microsecondAboveNow);
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
        Assert.Equal(microsecondAboveNow, result.Value.Allocation.HoldExpiresAt);
        Assert.Single(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_ExpiryAfterStartOfDayUtc_Fails()
    {
        var (service, assets, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(FixedToday.AddDays(5));
        var req = new CreateHoldRequest("sys", "ref-after-start", FixedToday.AddDays(5), FixedToday.AddDays(7), startOfDayUtc.AddHours(1));
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_AFTER_START", result.Error.Code);
    }

    [Fact]
    public async Task CreateHold_BookingsDisabled_Returns400()
    {
        var (service, assets, _, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId, acceptingBookings: false));

        var req = new CreateHoldRequest("sys", "ref-nobook", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2));
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_NOT_ACCEPTING_BOOKINGS", result.Error.Code);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
    }

    [Theory]
    [InlineData(AllocationStatus.Held, 4, 6)]   // Overlaps start
    [InlineData(AllocationStatus.Held, 6, 8)]   // Inside
    [InlineData(AllocationStatus.Held, 8, 11)]  // Overlaps end
    [InlineData(AllocationStatus.Held, 5, 5)]   // Boundary start day
    [InlineData(AllocationStatus.Held, 10, 10)] // Boundary end day
    [InlineData(AllocationStatus.Allocated, 4, 6)]
    [InlineData(AllocationStatus.Allocated, 6, 8)]
    [InlineData(AllocationStatus.Allocated, 8, 11)]
    [InlineData(AllocationStatus.Allocated, 5, 5)]
    [InlineData(AllocationStatus.Allocated, 10, 10)]
    public async Task CreateHold_Occupancy_ActiveHeldOrAllocated_Conflicts(AllocationStatus status, int reqStartOffset, int reqEndOffset)
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var existingPeriod = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(10));
        AssetAllocation existing;
        if (status == AllocationStatus.Held)
        {
            existing = AssetAllocation.CreateHold(
                Guid.NewGuid(), assetId, existingPeriod, FixedNow.AddHours(5), FixedNow, "partner", "EXT-OCC-1").Value;
        }
        else
        {
            existing = AssetAllocation.CreateManual(
                Guid.NewGuid(), assetId, existingPeriod, FixedNow, "EXT-OCC-1").Value;
        }
        allocs.Add(existing);

        var candidateReq = new CreateHoldRequest(
            "sys", "ref-candidate", FixedToday.AddDays(reqStartOffset), FixedToday.AddDays(reqEndOffset), FixedNow.AddHours(2));
        var result = await service.CreateHoldAsync(assetId, candidateReq, caller);

        Assert.True(result.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Theory]
    [InlineData("ExpiredHeld")]
    [InlineData("Released")]
    [InlineData("Cancelled")]
    public async Task CreateHold_Occupancy_ExpiredHeld_Released_OrCancelled_DoesNotBlock(string nonOccupyingType)
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var existingPeriod = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(10));
        AssetAllocation existing;
        if (nonOccupyingType == "ExpiredHeld")
        {
            existing = AssetAllocation.CreateHold(
                Guid.NewGuid(), assetId, existingPeriod, FixedNow.AddHours(-1), FixedNow.AddHours(-3), "partner", "EXT-NON-1").Value;
        }
        else if (nonOccupyingType == "Released")
        {
            existing = AssetAllocation.CreateManual(
                Guid.NewGuid(), assetId, existingPeriod, FixedNow.AddHours(-2), "EXT-NON-1").Value;
            Assert.True(existing.Release("Done", FixedNow.AddHours(-1), "operator").IsSuccess);
        }
        else
        {
            existing = AssetAllocation.CreateManual(
                Guid.NewGuid(), assetId, existingPeriod, FixedNow.AddHours(-2), "EXT-NON-1").Value;
            Assert.True(existing.Cancel("Cancelled", FixedNow.AddHours(-1), "operator").IsSuccess);
        }
        allocs.Add(existing);

        // Same period must succeed because the existing one does NOT occupy calendar!
        var candidateReq = new CreateHoldRequest(
            "sys", "ref-ok-same-period", existingPeriod.StartDate, existingPeriod.EndDate, FixedNow.AddHours(2));
        var result = await service.CreateHoldAsync(assetId, candidateReq, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
        Assert.Single(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_Occupancy_ExactExpiryHeld_DoesNotBlock()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var existingPeriod = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(10));
        // HoldExpiresAt == FixedNow (exact expiry boundary)
        var exactExpiryHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, existingPeriod, FixedNow, FixedNow.AddHours(-1), "partner", "EXT-EXACT").Value;
        allocs.Add(exactExpiryHold);

        var candidateReq = new CreateHoldRequest(
            "sys", "ref-exact-ok", existingPeriod.StartDate, existingPeriod.EndDate, FixedNow.AddHours(2));
        var result = await service.CreateHoldAsync(assetId, candidateReq, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
    }

    [Fact]
    public async Task CreateHold_OverlapSanitization_DoesNotLeakConflictDetails()
    {
        var (service, assets, allocs, blocks, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        // 1. Conflict with CalendarBlock
        var block = CalendarBlock.Create(
            Guid.NewGuid(), assetId, FixedToday.AddDays(5), FixedToday.AddDays(8),
            CalendarBlockReason.Maintenance, "user-secret-123", FixedNow, FixedToday, "Confidential note").Value;
        blocks.Add(block);

        var reqBlock = new CreateHoldRequest("sys", "ref-block", FixedToday.AddDays(6), FixedToday.AddDays(7), FixedNow.AddHours(2));
        var resBlock = await service.CreateHoldAsync(assetId, reqBlock, caller);

        Assert.True(resBlock.IsFailure);
        Assert.Equal("ASSET_BLOCKED", resBlock.Error.Code);
        Assert.Equal(ErrorType.Conflict, resBlock.Error.Type);
        Assert.DoesNotContain("Confidential note", resBlock.Error.Message);
        Assert.DoesNotContain("user-secret", resBlock.Error.Message);
        Assert.DoesNotContain(CalendarBlockReason.Maintenance.ToString(), resBlock.Error.Message);

        // 2. Conflict with existing Allocation
        blocks.Store.Clear();
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, new RentalPeriod(FixedToday.AddDays(10), FixedToday.AddDays(15)),
            FixedNow.AddHours(10), FixedNow, "partner-sys", "SECRET-EXTERNAL-REF", "Secret notes").Value;
        allocs.Add(existingHold);

        var reqAlloc = new CreateHoldRequest("sys", "ref-alloc", FixedToday.AddDays(12), FixedToday.AddDays(14), FixedNow.AddHours(2));
        var resAlloc = await service.CreateHoldAsync(assetId, reqAlloc, caller);

        Assert.True(resAlloc.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", resAlloc.Error.Code);
        Assert.Equal(ErrorType.Conflict, resAlloc.Error.Type);
        Assert.DoesNotContain("SECRET-EXTERNAL-REF", resAlloc.Error.Message);
        Assert.DoesNotContain("partner-sys", resAlloc.Error.Message);
        Assert.DoesNotContain("Secret notes", resAlloc.Error.Message);
    }

    [Fact]
    public async Task CreateHold_Success_EmitsOutboxAndReturns201Result()
    {
        var (service, assets, _, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var req = new CreateHoldRequest("sys", "ref-new", FixedToday.AddDays(5), FixedToday.AddDays(7), FixedNow.AddHours(2), "Test note");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
        var resp = result.Value.Allocation;
        Assert.Equal(assetId, resp.AssetId);
        Assert.Equal("sys", resp.SourceSystem);
        Assert.Equal("ref-new", resp.ExternalReference);
        Assert.Equal("Held", resp.Status);
        Assert.Equal("Test note", resp.Notes);
        Assert.Equal(FixedToday.AddDays(5), resp.StartDate);
        Assert.Equal(FixedToday.AddDays(7), resp.EndDate);

        Assert.Single(tx.StagedOutbox);
        var outbox = tx.StagedOutbox[0];
        Assert.Equal(assetId.ToString("D"), outbox.KafkaKey);
        Assert.Equal("inventory.asset.events.v1", outbox.Topic);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_ActiveHeld_Returns200ResultWithoutOutbox()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(2);
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiry, FixedNow, "sys", "ref-replay", "Notes").Value;
        allocs.Add(existingHold);

        var req = new CreateHoldRequest("sys", "ref-replay", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(existingHold.Id, result.Value.Allocation.Id);
        Assert.Equal("Held", result.Value.Allocation.Status);
        Assert.Equal(existingHold.CreatedAt, result.Value.Allocation.CreatedAt);
        Assert.Equal(existingHold.HoldExpiresAt, result.Value.Allocation.HoldExpiresAt);
        Assert.Empty(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_ExpiredHeld_ReturnsCurrentAllocation_WithoutNewTransitionOrOutbox()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(-1); // Expired!
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiry, FixedNow.AddHours(-3), "sys", "ref-expired-replay", "Notes").Value;
        allocs.Add(existingHold);

        var req = new CreateHoldRequest("sys", "ref-expired-replay", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(existingHold.Id, result.Value.Allocation.Id);
        Assert.Equal("Held", result.Value.Allocation.Status);
        Assert.Equal(expiry, result.Value.Allocation.HoldExpiresAt);
        Assert.Single(existingHold.Transitions); // No new transition appended
        Assert.Empty(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_AllocatedHold_ReturnsCurrentAllocatedResponse()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(2);
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiry, FixedNow.AddHours(-2), "sys", "ref-alloc-replay", "Notes").Value;
        Assert.True(existingHold.Allocate(FixedNow.AddHours(-1), "system").IsSuccess);
        allocs.Add(existingHold);

        var req = new CreateHoldRequest("sys", "ref-alloc-replay", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(existingHold.Id, result.Value.Allocation.Id);
        Assert.Equal("Allocated", result.Value.Allocation.Status);
        Assert.Equal(2, existingHold.Transitions.Count); // Transitions unchanged
        Assert.Empty(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_BookingsDisabled_ReturnsCurrentResponse()
    {
        var (service, assets, allocs, _, tx, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId, acceptingBookings: false)); // Bookings disabled!

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(2);
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiry, FixedNow, "sys", "ref-nobook-replay", "Notes").Value;
        allocs.Add(existingHold);

        var req = new CreateHoldRequest("sys", "ref-nobook-replay", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(existingHold.Id, result.Value.Allocation.Id);
        Assert.Empty(tx.StagedOutbox);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_SameInstantDifferentTimezoneOffset_Succeeds()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiryUtc = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiryUtc, FixedNow, "sys", "ref-offset", "Notes").Value;
        allocs.Add(existingHold);

        // Same instant represented in UTC+7 (19:00 +07:00 == 12:00 +00:00)
        var expiryIct = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.FromHours(7));
        var req = new CreateHoldRequest("sys", "ref-offset", FixedToday.AddDays(5), FixedToday.AddDays(7), expiryIct, "Notes");
        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(existingHold.Id, result.Value.Allocation.Id);
    }

    [Fact]
    public async Task CreateHold_ReusedExternalReference_DifferentPayload_Returns409Conflict()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(2);
        var existingHold = AssetAllocation.CreateHold(
            Guid.NewGuid(), assetId, period, expiry, FixedNow, "sys", "ref-mismatch", "Notes").Value;
        allocs.Add(existingHold);

        // Different end date
        var reqDiffDate = new CreateHoldRequest("sys", "ref-mismatch", FixedToday.AddDays(5), FixedToday.AddDays(8), expiry, "Notes");
        var res1 = await service.CreateHoldAsync(assetId, reqDiffDate, caller);
        Assert.True(res1.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", res1.Error.Code);
        Assert.Equal(ErrorType.Conflict, res1.Error.Type);

        // Different notes
        var reqDiffNotes = new CreateHoldRequest("sys", "ref-mismatch", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Different notes");
        var res2 = await service.CreateHoldAsync(assetId, reqDiffNotes, caller);
        Assert.True(res2.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", res2.Error.Code);

        // Different assetId
        var otherAssetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(otherAssetId));
        var reqDiffAsset = new CreateHoldRequest("sys", "ref-mismatch", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");
        var res3 = await service.CreateHoldAsync(otherAssetId, reqDiffAsset, caller);
        Assert.True(res3.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", res3.Error.Code);
    }

    [Fact]
    public async Task CreateHold_UniqueKeyRaceRetry_SucceedsWhenRowMatches()
    {
        var (service, assets, allocs, _, _, _) = CreateService();
        var caller = new TestCaller();
        var assetId = Guid.NewGuid();
        assets.Add(AllocationTestHelper.CreateAsset(assetId));

        var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(7));
        var expiry = FixedNow.AddHours(2);
        var req = new CreateHoldRequest("sys", "ref-race", FixedToday.AddDays(5), FixedToday.AddDays(7), expiry, "Notes");

        int callCount = 0;
        service = new InventoryAllocationService(allocs, new FakeWriteTransaction(assets, allocs, new FakeCalendarBlockRepository())
        {
            InterceptBeforeOperation = (id) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var competitorHold = AssetAllocation.CreateHold(
                        Guid.NewGuid(), assetId, period, expiry, FixedNow, "sys", "ref-race", "Notes").Value;
                    allocs.Add(competitorHold);
                    return Task.FromResult<Result?>(Error.Conflict("INVENTORY_DUPLICATE_KEY", "Duplicate key"));
                }
                return Task.FromResult<Result?>(null);
            }
        }, new TestTimeProvider(FixedNow));

        var result = await service.CreateHoldAsync(assetId, req, caller);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(2, callCount);
    }
}
