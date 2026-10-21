using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Frozen external client contract between RentalService and InventoryService (v1).
/// </summary>
public interface IInventoryRentalClient
{
    Task<Result<ListingAvailabilityCheckResponse>> CheckListingAvailabilityAsync(
        ListingAvailabilityCheckRequest request,
        CancellationToken ct = default);

    Task<Result<ListingRentalSnapshotsResponse>> GetListingRentalSnapshotsAsync(
        ListingRentalSnapshotsRequest request,
        CancellationToken ct = default);

}
