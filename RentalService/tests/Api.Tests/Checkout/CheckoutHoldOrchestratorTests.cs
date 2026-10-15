using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class CheckoutHoldOrchestratorTests
{
    // 10:00 ngày 09/10/2026 giờ Việt Nam.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 12);
    private static readonly DateOnly End = new(2026, 10, 15);
    private const string OperationId = "idem-key-1";

    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _cameraListingId = Guid.NewGuid();
    private readonly Guid _lightListingId = Guid.NewGuid();
    private readonly Guid _tripodListingId = Guid.NewGuid();
    private readonly FakeInventoryRentalClient _inventory = new();
    private readonly CheckoutPlan _plan;

    public CheckoutHoldOrchestratorTests()
    {
        _plan = Plan(_renterId, Line(_cameraListingId, 2), Line(_lightListingId, 1), Line(_tripodListingId, 1));
    }

    private static CheckoutPlanLine Line(Guid listingId, int quantity, DateOnly? start = null) => new(
        Guid.NewGuid(),
        listingId,
        Guid.NewGuid(),
        quantity,
        start ?? Start,
        End,
        "Listing",
        TestListingSnapshots.Default,
        500_000m,
        2_000_000m,
        3,
        500_000m * quantity * 3,
        2_000_000m * quantity);

    private static CheckoutPlan Plan(Guid renterId, params CheckoutPlanLine[] lines) => new(
        renterId,
        Guid.NewGuid(),
        new("Hồ Chí Minh", "123 Nguyễn Huệ, Quận 1", "Nguyễn Văn A", "0901234567", null),
        new("VCB", "0123456789", "NGUYEN VAN A"),
        "v1.0",
        "terms/v1.0.pdf",
        "hash123456",
        "VND",
        lines);

    private CheckoutHoldOrchestrator CreateOrchestrator() =>
        new(_inventory, NullLogger<CheckoutHoldOrchestrator>.Instance);

    [Fact]
    public async Task HoldAllLines_HoldsRequestedQuantityOfEveryLine_UntilPaymentDeadline()
    {
        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(30), result.HoldExpiresAt);
        Assert.Empty(result.Conflicts);
        Assert.Empty(_inventory.Releases);
        Assert.Equal(3, result.HeldLines.Count);

        for (int i = 0; i < _plan.Lines.Count; i++)
        {
            CheckoutPlanLine line = _plan.Lines[i];
            CheckoutHeldLine held = result.HeldLines[i];
            CreateHoldRequest request = _inventory.HoldRequests[i];

            Assert.Same(line, held.Line);
            Assert.NotEqual(Guid.Empty, held.RentalItemId);
            Assert.Equal(line.Quantity, held.AssignedAssets.Count);
            // Mã nhóm Rental lưu chính là groupReference gửi Inventory.
            Assert.Equal($"rental-item:{held.RentalItemId}", held.AllocationGroupId);

            Assert.Equal(line.EquipmentListingId, request.ListingId);
            Assert.Equal(line.Quantity, request.Quantity);
            // Đơn lưu [12, 15); Inventory nhận ngày dùng máy cuối có tính: 12 đến 14.
            Assert.Equal(Start, request.StartDate);
            Assert.Equal(End.AddDays(-1), request.EndDate);
            Assert.Equal("rental", request.SourceSystem);
            Assert.Equal($"rental-item:{held.RentalItemId}", request.GroupReference);
            Assert.Equal(Now.AddMinutes(30), request.HoldExpiresAt);
        }

        Assert.Equal(3, result.HeldLines.Select(h => h.RentalItemId).Distinct().Count());
    }

    [Fact]
    public async Task HoldAllLines_ReleasesEarlierHolds_AndStops_WhenALineCannotBeHeld()
    {
        _inventory.HoldErrors[_lightListingId] = Error.Conflict(
            InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ thiết bị.");

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        Assert.Empty(result.HeldLines);
        Assert.Null(result.HoldExpiresAt);

        CheckoutHoldConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal(CheckoutHoldConflictCode.InsufficientAvailability, conflict.Code);
        Assert.Equal(_plan.Lines[1].CartItemId, conflict.CartItemId);
        Assert.Equal(_lightListingId, conflict.EquipmentListingId);
        Assert.Equal(1, conflict.RequestedQuantity);

        Assert.Equal([_cameraListingId, _lightListingId], _inventory.HoldRequests.Select(r => r.ListingId));
        Assert.Equal([_inventory.GroupIdFor(_cameraListingId)], _inventory.Releases.Select(r => r.AllocationGroupId));
    }

    [Fact]
    public async Task HoldAllLines_ReleasesInReverseOrder_WhenTheLastLineFails()
    {
        _inventory.HoldErrors[_tripodListingId] = Error.Conflict(
            InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ thiết bị.");

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        Assert.Equal(
            [_inventory.GroupIdFor(_lightListingId), _inventory.GroupIdFor(_cameraListingId)],
            _inventory.Releases.Select(r => r.AllocationGroupId));
    }

    [Theory]
    [InlineData(ErrorType.ServiceUnavailable)]
    [InlineData(ErrorType.Failure)]
    public async Task HoldAllLines_ReturnsDependencyUnavailable_AndReleases_WhenInventoryFails(ErrorType errorType)
    {
        _inventory.HoldErrors[_lightListingId] = new Error("Inventory.Timeout", "InventoryService không phản hồi.", errorType);

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.HeldLines);
        Assert.Equal([_inventory.GroupIdFor(_cameraListingId)], _inventory.Releases.Select(r => r.AllocationGroupId));
    }

    [Theory]
    [InlineData(InventoryContractSpec.ErrorCodes.InsufficientAvailability, CheckoutHoldConflictCode.InsufficientAvailability)]
    [InlineData(InventoryContractSpec.ErrorCodes.SnapshotVersionMismatch, CheckoutHoldConflictCode.ListingChanged)]
    [InlineData(InventoryContractSpec.ErrorCodes.ListingNotFound, CheckoutHoldConflictCode.ListingUnavailable)]
    [InlineData(InventoryContractSpec.ErrorCodes.InvalidHoldExpiry, CheckoutHoldConflictCode.StartDateTooSoon)]
    [InlineData(InventoryContractSpec.ErrorCodes.HoldExpired, CheckoutHoldConflictCode.OperationNotReusable)]
    [InlineData(InventoryContractSpec.ErrorCodes.ExternalReferenceReused, CheckoutHoldConflictCode.OperationNotReusable)]
    [InlineData("SOMETHING_NEW", CheckoutHoldConflictCode.HoldRejected)]
    public async Task HoldAllLines_MapsInventoryErrorCodeToConflictCode(string inventoryCode, CheckoutHoldConflictCode expected)
    {
        _inventory.HoldErrors[_cameraListingId] = Error.Conflict(inventoryCode, "Inventory từ chối.");

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        Assert.Equal(expected, Assert.Single(result.Conflicts).Code);
        Assert.Empty(_inventory.Releases);
    }

    [Fact]
    public async Task HoldAllLines_ReusesSameReferences_WhenRetriedWithSameOperationId()
    {
        CheckoutHoldOrchestrator orchestrator = CreateOrchestrator();

        CheckoutHoldResult first = await orchestrator.HoldAllLinesAsync(_plan, OperationId, Now);
        CheckoutHoldResult retry = await orchestrator.HoldAllLinesAsync(_plan, $" {OperationId} ", Now);

        Assert.True(retry.IsSuccess);
        Assert.Equal(first.HeldLines.Select(h => h.RentalItemId), retry.HeldLines.Select(h => h.RentalItemId));
        Assert.Equal(first.HeldLines.Select(h => h.AllocationGroupId), retry.HeldLines.Select(h => h.AllocationGroupId));
        Assert.Equal(
            first.HeldLines.SelectMany(h => h.AssignedAssets).Select(a => a.AssetId),
            retry.HeldLines.SelectMany(h => h.AssignedAssets).Select(a => a.AssetId));
        Assert.Equal(3, _inventory.CreatedGroupCount);
    }

    [Fact]
    public async Task HoldAllLines_DerivesDifferentRentalItemIds_ForDifferentOperationOrRenter()
    {
        CheckoutHoldOrchestrator orchestrator = CreateOrchestrator();
        CheckoutPlan otherRenterPlan = _plan with { RenterId = Guid.NewGuid() };

        Guid original = (await orchestrator.HoldAllLinesAsync(_plan, OperationId, Now)).HeldLines[0].RentalItemId;
        Guid otherOperation = (await orchestrator.HoldAllLinesAsync(_plan, "idem-key-2", Now)).HeldLines[0].RentalItemId;
        Guid otherRenter = (await orchestrator.HoldAllLinesAsync(otherRenterPlan, OperationId, Now)).HeldLines[0].RentalItemId;

        Assert.NotEqual(original, otherOperation);
        Assert.NotEqual(original, otherRenter);
    }

    [Fact]
    public async Task HoldAllLines_RejectsWithoutCallingInventory_WhenHoldWouldRunPastStartDate()
    {
        // 23:45 ngày 11/10 giờ Việt Nam: hạn giữ 00:15 ngày 12/10 đã lấn sang ngày giao 12/10.
        DateTimeOffset lateEvening = new(2026, 10, 11, 16, 45, 0, TimeSpan.Zero);
        CheckoutPlanLine laterLine = Line(_lightListingId, 1, new DateOnly(2026, 10, 13));
        CheckoutPlan plan = Plan(_renterId, Line(_cameraListingId, 2), laterLine);

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(plan, OperationId, lateEvening);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        CheckoutHoldConflict conflict = Assert.Single(result.Conflicts);
        Assert.Equal(CheckoutHoldConflictCode.StartDateTooSoon, conflict.Code);
        Assert.Equal(_cameraListingId, conflict.EquipmentListingId);
        Assert.Empty(_inventory.HoldRequests);
        Assert.Empty(_inventory.Releases);
    }

    [Fact]
    public async Task HoldAllLines_AllowsHoldThatExpiresBeforeStartDate()
    {
        // 23:15 ngày 11/10 giờ Việt Nam: hạn giữ 23:45 vẫn trước 00:00 ngày giao 12/10.
        DateTimeOffset evening = new(2026, 10, 11, 16, 15, 0, TimeSpan.Zero);

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, evening);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HoldAllLines_TreatsWrongAssignedCountAsDependencyFailure_AndReleasesThatGroupToo()
    {
        _inventory.AssignedCountOverrides[_lightListingId] = 3;

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.DependencyUnavailable, result.Error);
        Assert.Equal(
            [_inventory.GroupIdFor(_lightListingId), _inventory.GroupIdFor(_cameraListingId)],
            _inventory.Releases.Select(r => r.AllocationGroupId));
        Assert.Equal(2, _inventory.HoldRequests.Count);
    }

    [Fact]
    public async Task HoldAllLines_KeepsOriginalError_WhenReleaseFails()
    {
        _inventory.FailRelease = true;
        _inventory.HoldErrors[_tripodListingId] = Error.Conflict(
            InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ thiết bị.");

        CheckoutHoldResult result = await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now);

        Assert.Equal(CheckoutErrors.HoldFailed, result.Error);
        Assert.Equal(2, _inventory.Releases.Count);
    }

    [Fact]
    public async Task HoldAllLines_DoesNotPassRequestCancellationToRelease()
    {
        using CancellationTokenSource cts = new();
        _inventory.HoldErrors[_lightListingId] = Error.Conflict(
            InventoryContractSpec.ErrorCodes.InsufficientAvailability, "Không đủ thiết bị.");

        await CreateOrchestrator().HoldAllLinesAsync(_plan, OperationId, Now, cts.Token);

        Assert.True(_inventory.HoldTokens.All(t => t == cts.Token));
        Assert.All(_inventory.Releases, r => Assert.False(r.Token.CanBeCanceled));
    }

    [Fact]
    public async Task ReleaseAllLines_UsesStableOperationKeyPerGroup()
    {
        CheckoutHoldOrchestrator orchestrator = CreateOrchestrator();
        CheckoutHoldResult held = await orchestrator.HoldAllLinesAsync(_plan, OperationId, Now);

        await orchestrator.ReleaseAllLinesAsync(held.HeldLines);
        await orchestrator.ReleaseAllLinesAsync(held.HeldLines);

        Assert.Equal(6, _inventory.Releases.Count);
        Assert.Equal(3, _inventory.Releases.Select(r => r.Request.OperationKey).Distinct().Count());
        Assert.All(
            _inventory.Releases.GroupBy(r => r.AllocationGroupId),
            g => Assert.Single(g.Select(r => r.Request.OperationKey).Distinct()));
        Assert.All(_inventory.Releases, r => Assert.False(string.IsNullOrWhiteSpace(r.Request.Reason)));
    }

    [Fact]
    public async Task HoldAllLines_RejectsBlankOperationId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateOrchestrator().HoldAllLinesAsync(_plan, " ", Now));
    }
}
