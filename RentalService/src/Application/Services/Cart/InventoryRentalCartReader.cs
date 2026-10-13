using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.Services.Cart;

/// <summary>Adapts the frozen Inventory contract to Cart's read and ownership ports.</summary>
public sealed class InventoryRentalCartReader(IInventoryRentalClient inventory) :
    IRentalListingOwnerProvider,
    IRentalListingReader,
    IRentalAvailabilityReader
{
    public async Task<Guid?> GetOwnerIdAsync(Guid equipmentListingId, CancellationToken cancellationToken = default)
    {
        Result<ListingRentalSnapshotDto> result = await inventory.GetListingRentalSnapshotAsync(
            equipmentListingId, cancellationToken);

        if (result.IsFailure)
        {
            if (result.Error.Type == ErrorType.NotFound) return null;
            throw DependencyFailure(result.Error);
        }

        return result.Value.SupplierId == Guid.Empty ? null : result.Value.SupplierId;
    }

    public async Task<RentalListingSnapshot?> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        Result<ListingRentalSnapshotDto> result = await inventory.GetListingRentalSnapshotAsync(
            listingId, cancellationToken);

        if (result.IsFailure)
        {
            if (result.Error.Type == ErrorType.NotFound) return null;
            throw DependencyFailure(result.Error);
        }

        ListingRentalSnapshotDto snapshot = result.Value;
        bool active = string.Equals(snapshot.BookingStatus, "Active", StringComparison.OrdinalIgnoreCase);

        return new RentalListingSnapshot(
            snapshot.ListingId,
            snapshot.SupplierId,
            IsPublished: active,
            AcceptsBookings: active,
            snapshot.DailyRate,
            snapshot.DepositPerUnit,
            snapshot.SnapshotVersion);
    }

    public async Task<int?> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        Result<(DateOnly From, DateOnly To)> rentalDates = RentalInventoryDateAdapter.ToRentalDates(startDate, endDate);
        if (rentalDates.IsFailure) return null;

        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(
                listingId,
                rentalDates.Value.From,
                rentalDates.Value.To,
                Quantity: 1),
            cancellationToken);

        if (result.IsFailure) throw DependencyFailure(result.Error);
        return result.Value.AvailableQuantity;
    }

    private static InvalidOperationException DependencyFailure(Error error) =>
        new($"Inventory contract call failed ({error.Code}).");
}
