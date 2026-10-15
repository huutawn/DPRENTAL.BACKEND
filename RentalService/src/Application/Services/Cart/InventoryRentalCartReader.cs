using System.Globalization;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Policies;
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

        return result.Value.OwnerUserId == Guid.Empty ? null : result.Value.OwnerUserId;
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
        bool active = string.Equals(snapshot.Status, "Active", StringComparison.OrdinalIgnoreCase);

        // Inventory không có phiên bản giá: lấy chính giá thuê làm phiên bản, giá đổi thì phiên bản đổi.
        return new RentalListingSnapshot(
            snapshot.Id,
            snapshot.OwnerUserId,
            IsPublished: active,
            AcceptsBookings: active,
            snapshot.DailyRate,
            CheckoutRules.DepositPerUnit,
            snapshot.DailyRate.ToString(CultureInfo.InvariantCulture));
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
