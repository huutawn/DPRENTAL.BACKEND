using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
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
    public async Task<Result<RentalListingSnapshot>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        Result<ListingRentalSnapshotsResponse> result = await inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest([listingId]), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        var snapshot = result.Value.Items.FirstOrDefault(x => x.Id == listingId);
        if (snapshot is null)
        {
            return Error.NotFound("LISTING_NOT_FOUND", "Sản phẩm không tồn tại hoặc đã bị xóa.");
        }

        bool active = string.Equals(snapshot.Status, "Active", StringComparison.OrdinalIgnoreCase);

        // Inventory không có phiên bản giá: lấy chính giá thuê làm phiên bản, giá đổi thì phiên bản đổi.
        return Result.Success(new RentalListingSnapshot(
            snapshot.Id,
            snapshot.OwnerUserId,
            snapshot.EquipmentTypeId,
            active,
            active,
            snapshot.DailyRate,
            CheckoutRules.DepositPerUnit,
            snapshot.DailyRate.ToString(CultureInfo.InvariantCulture)));
    }

    public async Task<Result<int>> GetAvailableQuantityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (startDate > endDate)
        {
            return Error.Validation("INVALID_DATES", "Ngày bắt đầu không được lớn hơn ngày kết thúc.");
        }

        // Giỏ và Inventory cùng tính ngày cuối có tính, nên gửi thẳng ngày của giỏ.
        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(listingId, startDate, endDate, RequestedQuantity: 1),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        return Result.Success(result.Value.AvailableQuantity);
    }
}
