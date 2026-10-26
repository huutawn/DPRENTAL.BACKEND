using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Security;

namespace Dprental.InventoryService.Application.IServices;

/// <summary>
/// Ghi nhận các tín hiệu hành vi duyệt và tìm kiếm (ListingViewed, SearchPerformed) vào Transactional Outbox.
/// </summary>
public interface IBehaviorEventRecorder
{
    Task RecordListingViewedAsync(
        Guid listingId,
        Guid equipmentTypeId,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default);

    Task RecordSearchPerformedAsync(
        ListPublicEquipmentListingsRequest request,
        int totalResultCount,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default);
}
