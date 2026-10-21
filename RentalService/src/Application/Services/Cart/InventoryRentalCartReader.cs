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
        ListingRentalSnapshotDto? listing = await ReadListingAsync(equipmentListingId, cancellationToken);
        return listing is null || listing.OwnerUserId == Guid.Empty ? null : listing.OwnerUserId;
    }

    public async Task<RentalListingSnapshot?> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        ListingRentalSnapshotDto? snapshot = await ReadListingAsync(listingId, cancellationToken);
        if (snapshot is null) return null;

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
        if (startDate > endDate) return null;

        // Giỏ và Inventory cùng tính ngày cuối có tính, nên gửi thẳng ngày của giỏ.
        Result<ListingAvailabilityCheckResponse> result = await inventory.CheckListingAvailabilityAsync(
            new ListingAvailabilityCheckRequest(listingId, startDate, endDate, RequestedQuantity: 1),
            cancellationToken);

        if (result.IsFailure) throw DependencyFailure(result.Error);
        return result.Value.AvailableQuantity;
    }

    /// <summary>
    /// Đọc một Listing qua API batch của Inventory (danh sách chỉ có một mã). Listing nằm trong
    /// <c>missingIds</c> thì trả null, như không tìm thấy.
    /// </summary>
    private async Task<ListingRentalSnapshotDto?> ReadListingAsync(Guid listingId, CancellationToken cancellationToken)
    {
        // Inventory từ chối mã rỗng (400); coi như không tìm thấy để giỏ báo đúng lỗi của mình.
        if (listingId == Guid.Empty) return null;

        Result<ListingRentalSnapshotsResponse> result = await inventory.GetListingRentalSnapshotsAsync(
            new ListingRentalSnapshotsRequest([listingId]), cancellationToken);

        if (result.IsFailure) throw DependencyFailure(result.Error);

        ListingRentalSnapshotsResponse response = result.Value;
        if (response.MissingIds?.Contains(listingId) == true) return null;

        ListingRentalSnapshotDto? listing = response.Items?.SingleOrDefault(i => i?.Id == listingId);
        return listing ?? throw DependencyFailure(Error.Failure(
            "Inventory.InvalidBatchResponse", "Inventory không trả Listing đã hỏi trong items lẫn missingIds."));
    }

    private static InvalidOperationException DependencyFailure(Error error) =>
        new($"Inventory contract call failed ({error.Code}).");
}
