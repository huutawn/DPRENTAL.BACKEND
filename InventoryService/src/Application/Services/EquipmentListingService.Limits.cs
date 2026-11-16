using Dprental.InventoryService.Application.Policies;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;

namespace Dprental.InventoryService.Application.Services;

public sealed partial class EquipmentListingService
{
    /// <summary>
    /// Áp hạn mức bản nháp cho người chưa có tổ chức được duyệt. Thứ tự khóa phải giữ nguyên:
    /// Listing -> Warehouse(chủ), giống CreateListingAsync; không lấy khóa chủ sở hữu trước khóa listing.
    /// Câu đếm phải chạy sau khi lấy khóa để thấy dữ liệu đã commit của thao tác trước.
    /// </summary>
    private async Task<Result> EnforceUnapprovedLimitsAsync(
        IInventoryWriteContext ctx,
        IInventoryCallerContext caller,
        Guid ownerUserId,
        int addingListings,
        int addingAssets,
        CancellationToken cancellationToken)
    {
        if (!UnapprovedInventoryLimitPolicy.Applies(caller.IsAdmin, caller.OrganizationId))
        {
            return Result.Success();
        }

        await ctx.AcquireWarehouseLockAsync(ownerUserId, cancellationToken);
        var counts = await ctx.GetOwnerInventoryCountsAsync(ownerUserId, cancellationToken);

        if (addingListings > 0)
        {
            var listingCheck = UnapprovedInventoryLimitPolicy.CheckListings(counts.CountedListings, counts.AllListings, addingListings, _limitOptions);
            if (listingCheck.IsFailure)
            {
                return listingCheck;
            }
        }

        if (addingAssets > 0)
        {
            var assetCheck = UnapprovedInventoryLimitPolicy.CheckAssets(counts.CountedAssets, counts.AllAssets, addingAssets, _limitOptions);
            if (assetCheck.IsFailure)
            {
                return assetCheck;
            }
        }

        return Result.Success();
    }
}
