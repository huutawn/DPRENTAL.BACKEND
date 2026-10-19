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
    IRentalListingReader,
    IRentalAvailabilityReader
{
    public async Task<RentalListingSnapshot?> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        Result<ListingRentalSnapshotsResponse> result = await inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest([listingId]), cancellationToken);

        if (result.IsFailure)
        {
            if (result.Error.Type == ErrorType.NotFound) return null;
            throw DependencyFailure(result.Error);
        }

        var snapshot = result.Value.Items.FirstOrDefault(x => x.Id == listingId);
        if (snapshot is null) return null;

        bool active = string.Equals(snapshot.Status, "Active", StringComparison.OrdinalIgnoreCase);

        // Inventory không có phiên bản giá: lấy chính giá thuê làm phiên bản, giá đổi thì phiên bản đổi.
        return new RentalListingSnapshot(
            snapshot.Id,
            snapshot.OwnerUserId,
            snapshot.EquipmentTypeId,
            active,
            active,
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
        if (startDate > endDate) return null;

        // Giỏ và Inventory cùng tính ngày cuối có tính, nên gửi thẳng ngày của giỏ.
        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(listingId, startDate, endDate, RequestedQuantity: 1),
            cancellationToken);

        if (result.IsFailure) throw DependencyFailure(result.Error);
        return result.Value.AvailableQuantity;
    }

    private static InvalidOperationException DependencyFailure(Error error) =>
        new($"Inventory contract call failed ({error.Code}).");
}
