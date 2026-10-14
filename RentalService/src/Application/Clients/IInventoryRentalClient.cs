using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Frozen external client contract between RentalService and InventoryService (v1).
/// </summary>
public interface IInventoryRentalClient
{
    // Read & Hold Operations
    Task<Result<ListingRentalSnapshotDto>> GetListingRentalSnapshotAsync(
        Guid listingId,
        CancellationToken ct = default);

    Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(
        ListingAvailabilityCheckRequest request,
        CancellationToken ct = default);

    Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(
        ListingRentalSnapshotsRequest request,
        CancellationToken ct = default);

    Task<Result<ListingAvailabilityBatchResponse>> CheckListingAvailabilityBatchAsync(
        ListingAvailabilityBatchRequest request,
        CancellationToken ct = default);

    Task<Result<CreateHoldResponse>> CreateHoldAsync(
        CreateHoldRequest request,
        CancellationToken ct = default);

    Task<Result<ExtendHoldResponse>> ExtendHoldAsync(
        string allocationGroupId,
        ExtendHoldRequest request,
        CancellationToken ct = default);

    Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(
        string allocationGroupId,
        ConfirmAllocationRequest request,
        CancellationToken ct = default);

    // Closure, Overdue & Damage Operations
    Task<Result<ReleaseAllocationResponse>> ReleaseAllocationAsync(
        string allocationGroupId,
        ReleaseAllocationRequest request,
        CancellationToken ct = default);

    Task<Result<CancelAllocationResponse>> CancelAllocationAsync(
        string allocationGroupId,
        CancelAllocationRequest request,
        CancellationToken ct = default);

    Task<Result<MarkAssetsOverdueResponse>> MarkAssetsOverdueAsync(
        string allocationGroupId,
        MarkAssetsOverdueRequest request,
        CancellationToken ct = default);

    Task<Result<MarkAssetsReturnedResponse>> MarkAssetsReturnedAsync(
        string allocationGroupId,
        MarkAssetsReturnedRequest request,
        CancellationToken ct = default);

    Task<Result<BlockDamagedAssetsResponse>> BlockDamagedAssetsAsync(
        string allocationGroupId,
        BlockDamagedAssetsRequest request,
        CancellationToken ct = default);
}
