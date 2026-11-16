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

    Task<Result<CreateHoldResponse>> CreateHoldAsync(
        CreateHoldRequest request,
        CancellationToken ct = default);

    Task<Result<ConfirmAllocationResponse>> ConfirmAllocationAsync(
        string allocationGroupId,
        ConfirmAllocationRequest request,
        CancellationToken ct = default);

    Task<Result<WarehouseDeliveryPolicyResponse>> GetWarehouseDeliveryPolicyAsync(
        Guid supplierId,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Success(new WarehouseDeliveryPolicyResponse(
            supplierId,
            true,
            new WarehouseAddressDto("123 Warehouse Rd", "79", "Ward 1"),
            new OperatingHoursDto("08:00", "18:00", []),
            [
                new DeliveryZoneDto("79", null, 0m, 0m),
                new DeliveryZoneDto("Hồ Chí Minh", null, 0m, 0m)
            ])));
}
