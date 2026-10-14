using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Cổng đọc EquipmentListing và availability từ InventoryService phục vụ checkout.
/// Result thất bại nghĩa là không gọi được Inventory hoặc response sai hợp đồng.
/// </summary>
public interface IInventoryListingClient
{
    /// <summary>Đọc Listing hiện tại; trả <c>null</c> khi Listing không tồn tại.</summary>
    Task<Result<InventoryListingDto?>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>Hỏi số physical asset rảnh xuyên suốt khoảng ngày thuê [startDate, endDate) theo quy ước của Rental.</summary>
    Task<Result<InventoryListingAvailabilityDto>> CheckAvailabilityAsync(
        Guid listingId,
        DateOnly startDate,
        DateOnly endDate,
        int requestedQuantity,
        CancellationToken cancellationToken = default);
}
