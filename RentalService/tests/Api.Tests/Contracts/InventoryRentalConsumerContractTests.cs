using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Contracts;

/// <summary>
/// Consumer-side contract tests for the Rental–Inventory v1 boundary.
/// NOTE: In accordance with project architecture and the DPRental Agent Guide:
/// - These consumer tests verify client DTO serialization, route construction, and contract mapping using fixed fixtures and stubs.
/// - End-to-end provider availability, atomicity, and multi-unit locking are proven by the InventoryService provider conformance test suite.
/// </summary>
public sealed class InventoryRentalConsumerContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly StubInventoryRentalClient _stub = new();
    private readonly DateTimeOffset _fixedTime = new(2026, 10, 10, 8, 0, 0, TimeSpan.FromHours(7));

    // 1. Route Specification & Formatting Tests
    [Fact]
    public void Routes_GenerateExpectedInventoryEndpoints()
    {
        Guid listingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        string groupId = "group-test-01";

        Assert.Equal("/equipment-listings/11111111-1111-1111-1111-111111111111/rental-snapshot",
            InventoryContractSpec.Routes.ListingRentalSnapshot(listingId));

        Assert.Equal("/equipment-listings/11111111-1111-1111-1111-111111111111/availability/check",
            InventoryContractSpec.Routes.CheckAvailability(listingId));

        Assert.Equal("/equipment-listings/11111111-1111-1111-1111-111111111111/holds",
            InventoryContractSpec.Routes.CreateHold(listingId));

        Assert.Equal("/allocation-groups/group-test-01/extend-hold",
            InventoryContractSpec.Routes.ExtendHold(groupId));

        Assert.Equal("/allocation-groups/group-test-01/confirm",
            InventoryContractSpec.Routes.ConfirmAllocation(groupId));

        Assert.Equal("/allocation-groups/group-test-01/release",
            InventoryContractSpec.Routes.ReleaseAllocation(groupId));

        Assert.Equal("/allocation-groups/group-test-01/cancel",
            InventoryContractSpec.Routes.CancelAllocation(groupId));

        Assert.Equal("/allocation-groups/group-test-01/mark-overdue",
            InventoryContractSpec.Routes.MarkOverdue(groupId));

        Assert.Equal("/allocation-groups/group-test-01/mark-returned",
            InventoryContractSpec.Routes.MarkReturned(groupId));

        Assert.Equal("/allocation-groups/group-test-01/block-damaged",
            InventoryContractSpec.Routes.BlockDamaged(groupId));
    }

    // 2. Listing Snapshot Fixture & Serialization
    [Fact]
    public async Task GetListingRentalSnapshot_MapsFromFixture_WithoutInternalAssetCodes()
    {
        Guid listingId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid supplierId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        ListingRentalSnapshotDto fixture = new(
            listingId,
            supplierId,
            "Sony FX3 Cinema Camera Kit",
            "Professional cinema body with 24-70mm lens",
            "District 1, Ho Chi Minh City",
            500_000m,
            1_000_000m,
            "VND",
            "Active",
            "v1.0",
            _fixedTime,
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            "FX3",
            2,
            """{"sensor":"full-frame"}""",
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

        _stub.GetSnapshotHandler = id => Task.FromResult(Result.Success(fixture));

        Result<ListingRentalSnapshotDto> result = await _stub.GetListingRentalSnapshotAsync(listingId);

        Assert.True(result.IsSuccess);
        Assert.Equal(500_000m, result.Value.DailyRate);
        Assert.Equal(1_000_000m, result.Value.DepositPerUnit);
        Assert.Equal("VND", result.Value.Currency);
        Assert.Equal("v1.0", result.Value.SnapshotVersion);

        // Verify JSON round-trip
        string json = JsonSerializer.Serialize(result.Value, JsonOptions);
        Assert.Contains("\"dailyRate\":500000", json);
        Assert.Contains("\"snapshotVersion\":\"v1.0\"", json);
        Assert.Contains("\"equipmentTypeId\":\"cccccccc-cccc-cccc-cccc-cccccccccccc\"", json);
        Assert.Contains("\"brandId\":\"dddddddd-dddd-dddd-dddd-dddddddddddd\"", json);
        Assert.Contains("\"model\":\"FX3\"", json);
        Assert.Contains("\"specSchemaVersion\":2", json);
        Assert.Contains("\"specValuesJson\":", json);
        Assert.Contains("\"thumbnailMediaId\":\"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee\"", json);
        Assert.DoesNotContain("serialNumber", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assetCode", json, StringComparison.OrdinalIgnoreCase);

        ListingRentalSnapshotDto? deserialized = JsonSerializer.Deserialize<ListingRentalSnapshotDto>(json, JsonOptions);
        Assert.NotNull(deserialized);
        Assert.Equal(fixture, deserialized);
    }

    [Fact]
    public async Task GetListingRentalSnapshot_WhenNotFound_MapsErrorCorrectly()
    {
        _stub.GetSnapshotHandler = _ => Task.FromResult(Result.Failure<ListingRentalSnapshotDto>(
            Error.NotFound(InventoryContractSpec.ErrorCodes.ListingNotFound, "Listing does not exist.")));

        Result<ListingRentalSnapshotDto> result = await _stub.GetListingRentalSnapshotAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal(InventoryContractSpec.ErrorCodes.ListingNotFound, result.Error.Code);
    }

    // 3. Availability Check Fixture & Serialization
    [Fact]
    public async Task CheckListingAvailability_SerializesDatesAndEvaluatesResponse()
    {
        Guid listingId = Guid.NewGuid();
        ListingAvailabilityCheckRequest request = new(
            listingId,
            new(2026, 10, 10),
            new(2026, 10, 15),
            2);

        ListingAvailabilityCheckResponse responseFixture = new(
            AvailableQuantity: 2,
            IsAvailable: true,
            EvaluatedAt: _fixedTime,
            From: request.From,
            To: request.To);

        _stub.CheckAvailabilityHandler = req => Task.FromResult(Result.Success(responseFixture));

        Result<ListingAvailabilityCheckResponse> result = await _stub.CheckListingAvailabilityAsync(request);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsAvailable);
        Assert.Equal(2, result.Value.AvailableQuantity);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        Assert.Contains("\"from\":\"2026-10-10\"", json);
        Assert.Contains("\"to\":\"2026-10-15\"", json);
        Assert.Contains("\"quantity\":2", json);
    }

    // 4. Create Hold Fixture & Mapping
    [Fact]
    public async Task CreateHold_MapsAssignedAssets_AndReplayFlag()
    {
        Guid listingId = Guid.NewGuid();
        Guid alloc1 = Guid.NewGuid();
        Guid asset1 = Guid.NewGuid();
        Guid alloc2 = Guid.NewGuid();
        Guid asset2 = Guid.NewGuid();

        CreateHoldRequest request = new(
            listingId,
            2,
            new(2026, 10, 10),
            new(2026, 10, 15),
            "rental-item:001",
            _fixedTime.AddMinutes(30),
            "v1.0");

        CreateHoldResponse responseFixture = new(
            AllocationGroupId: "group-hold-001",
            AssignedAssets:
            [
                new(alloc1, asset1, "Held"),
                new(alloc2, asset2, "Held")
            ],
            HoldExpiresAt: request.RequestedHoldExpiresAt,
            ListingId: listingId,
            Quantity: 2,
            From: request.From,
            To: request.To,
            IsReplay: false,
            AllocationStatus: "Held");

        _stub.CreateHoldHandler = _ => Task.FromResult(Result.Success(responseFixture));

        Result<CreateHoldResponse> result = await _stub.CreateHoldAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("group-hold-001", result.Value.AllocationGroupId);
        Assert.Equal(2, result.Value.AssignedAssets.Count);
        Assert.False(result.Value.IsReplay);

        // Verify JSON round-trip
        string json = JsonSerializer.Serialize(result.Value, JsonOptions);
        Assert.Contains("\"allocationGroupId\":\"group-hold-001\"", json);
        Assert.Contains("\"isReplay\":false", json);

        CreateHoldResponse? roundtrip = JsonSerializer.Deserialize<CreateHoldResponse>(json, JsonOptions);
        Assert.NotNull(roundtrip);
        Assert.Equal(2, roundtrip.AssignedAssets.Count);
    }

    // 5. Extend Hold Fixture
    [Fact]
    public async Task ExtendHold_MapsRequestAndResponse()
    {
        DateTimeOffset newExpiry = _fixedTime.AddHours(24);
        ExtendHoldRequest request = new(newExpiry, "op-extend-01");
        ExtendHoldResponse responseFixture = new("receipt-01", "group-001", newExpiry, _fixedTime, false);

        _stub.ExtendHoldHandler = (gId, req) => Task.FromResult(Result.Success(responseFixture));

        Result<ExtendHoldResponse> result = await _stub.ExtendHoldAsync("group-001", request);

        Assert.True(result.IsSuccess);
        Assert.Equal(newExpiry, result.Value.NewHoldExpiresAt);
        Assert.False(result.Value.IsReplay);
    }

    // 6. Confirm Allocation Fixture
    [Fact]
    public async Task ConfirmAllocation_MapsConfirmedUnits()
    {
        ConfirmAllocationRequest request = new("op-confirm-01");
        ConfirmAllocationResponse responseFixture = new(
            ReceiptRef: "receipt-02",
            AllocationGroupId: "group-001",
            AppliedAt: _fixedTime,
            IsReplay: false,
            Units: [new(Guid.NewGuid(), Guid.NewGuid(), "Confirmed")]);

        _stub.ConfirmAllocationHandler = (gId, req) => Task.FromResult(Result.Success(responseFixture));

        Result<ConfirmAllocationResponse> result = await _stub.ConfirmAllocationAsync("group-001", request);

        Assert.True(result.IsSuccess);
        Assert.Equal("group-001", result.Value.AllocationGroupId);
        Assert.Single(result.Value.Units);
        Assert.Equal("Confirmed", result.Value.Units[0].Status);
    }

    // 7. Release Allocation Fixture
    [Fact]
    public async Task ReleaseAllocation_MapsReleasedResponse()
    {
        ReleaseAllocationRequest request = new("op-release-01", "Rental item cancelled");
        ReleaseAllocationResponse responseFixture = new(
            ReceiptRef: "receipt-03",
            OperationKey: "op-release-01",
            AllocationGroupId: "group-001",
            AppliedAt: _fixedTime,
            IsReplay: false);

        _stub.ReleaseAllocationHandler = (gId, req) => Task.FromResult(Result.Success(responseFixture));

        Result<ReleaseAllocationResponse> result = await _stub.ReleaseAllocationAsync("group-001", request);

        Assert.True(result.IsSuccess);
        Assert.Equal("group-001", result.Value.AllocationGroupId);
        Assert.Equal("receipt-03", result.Value.ReceiptRef);
    }

    // 8. Mark Returned and Block Damaged Fixtures
    [Fact]
    public async Task MarkAssetsReturned_And_BlockDamaged_MapCorrectStatuses()
    {
        Guid unit1 = Guid.NewGuid();
        Guid unit2 = Guid.NewGuid();
        Guid returnRecordId = Guid.NewGuid();

        MarkAssetsReturnedRequest returnReq = new(
            returnRecordId,
            _fixedTime,
            [new(unit1, UnitCondition.Normal)],
            "op-ret-01");

        MarkAssetsReturnedResponse returnResp = new(
            "receipt-04",
            "op-ret-01",
            "group-001",
            _fixedTime,
            false,
            [new(unit1, Guid.NewGuid(), "Available", _fixedTime, null)]);

        _stub.MarkReturnedHandler = (gId, req) => Task.FromResult(Result.Success(returnResp));

        Result<MarkAssetsReturnedResponse> retResult = await _stub.MarkAssetsReturnedAsync("group-001", returnReq);
        Assert.True(retResult.IsSuccess);
        Assert.Equal("Available", retResult.Value.Units[0].Status);

        BlockDamagedAssetsRequest blockReq = new(
            [unit2],
            returnRecordId,
            "Damaged sensor",
            "op-block-01");

        BlockDamagedAssetsResponse blockResp = new(
            "receipt-05",
            "op-block-01",
            "group-001",
            _fixedTime,
            false,
            [new(unit2, Guid.NewGuid(), "Blocked", "DISP-DAM-001")]);

        _stub.BlockDamagedHandler = (gId, req) => Task.FromResult(Result.Success(blockResp));

        Result<BlockDamagedAssetsResponse> blockResult = await _stub.BlockDamagedAssetsAsync("group-001", blockReq);
        Assert.True(blockResult.IsSuccess);
        Assert.Equal("Blocked", blockResult.Value.Units[0].Status);
        Assert.Equal("DISP-DAM-001", blockResult.Value.Units[0].DamageDispositionRef);
    }

    // Stub Implementation
    private sealed class StubInventoryRentalClient : IInventoryRentalClient
    {
        public Func<Guid, Task<Result<ListingRentalSnapshotDto>>>? GetSnapshotHandler { get; set; }
        public Func<ListingAvailabilityCheckRequest, Task<Result<ListingAvailabilityCheckResponse>>>? CheckAvailabilityHandler { get; set; }
        public Func<CreateHoldRequest, Task<Result<CreateHoldResponse>>>? CreateHoldHandler { get; set; }
        public Func<string, ExtendHoldRequest, Task<Result<ExtendHoldResponse>>>? ExtendHoldHandler { get; set; }
        public Func<string, ConfirmAllocationRequest, Task<Result<ConfirmAllocationResponse>>>? ConfirmAllocationHandler { get; set; }
        public Func<string, ReleaseAllocationRequest, Task<Result<ReleaseAllocationResponse>>>? ReleaseAllocationHandler { get; set; }
        public Func<string, CancelAllocationRequest, Task<Result<CancelAllocationResponse>>>? CancelAllocationHandler { get; set; }
        public Func<string, MarkAssetsOverdueRequest, Task<Result<MarkAssetsOverdueResponse>>>? MarkOverdueHandler { get; set; }
        public Func<string, MarkAssetsReturnedRequest, Task<Result<MarkAssetsReturnedResponse>>>? MarkReturnedHandler { get; set; }
        public Func<string, BlockDamagedAssetsRequest, Task<Result<BlockDamagedAssetsResponse>>>? BlockDamagedHandler { get; set; }

        public Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(Guid listingId, CancellationToken ct = default) =>
            GetSnapshotHandler?.Invoke(listingId) ?? Task.FromResult(Result.Failure<ListingRentalSnapshotDto>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(ListingAvailabilityCheckRequest request, CancellationToken ct = default) =>
            CheckAvailabilityHandler?.Invoke(request) ?? Task.FromResult(Result.Failure<ListingAvailabilityCheckResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<CreateHoldResponse>> CreateHoldAsync(CreateHoldRequest request, CancellationToken ct = default) =>
            CreateHoldHandler?.Invoke(request) ?? Task.FromResult(Result.Failure<CreateHoldResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<ExtendHoldResponse>> ExtendHoldAsync(string allocationGroupId, ExtendHoldRequest request, CancellationToken ct = default) =>
            ExtendHoldHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<ExtendHoldResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(string allocationGroupId, ConfirmAllocationRequest request, CancellationToken ct = default) =>
            ConfirmAllocationHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<ConfirmAllocationResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(string allocationGroupId, ReleaseAllocationRequest request, CancellationToken ct = default) =>
            ReleaseAllocationHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<ReleaseAllocationResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<CancelAllocationResponse>> CancelAllocationAsync(string allocationGroupId, CancelAllocationRequest request, CancellationToken ct = default) =>
            CancelAllocationHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<CancelAllocationResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(string allocationGroupId, MarkAssetsOverdueRequest request, CancellationToken ct = default) =>
            MarkOverdueHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<MarkAssetsOverdueResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(string allocationGroupId, MarkAssetsReturnedRequest request, CancellationToken ct = default) =>
            MarkReturnedHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<MarkAssetsReturnedResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));

        public Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(string allocationGroupId, BlockDamagedAssetsRequest request, CancellationToken ct = default) =>
            BlockDamagedHandler?.Invoke(allocationGroupId, request) ?? Task.FromResult(Result.Failure<BlockDamagedAssetsResponse>(Error.NotFound("NotConfigured", "Stub not configured.")));
    }
}
