namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Bộ đánh giá quyền truy cập tập trung của Inventory Service (Access Evaluator).
/// Thực thi ma trận phân quyền DPR-147 dựa trên User Ownership (OwnerUserId) và Claims.
/// </summary>
public static class InventoryAccessEvaluator
{
    public const string PermissionAssetManage = "inventory.asset.manage";
    public const string ScopeAllocationWrite = "inventory.allocation.write";
    public const string ScopeAvailabilityRead = "inventory.availability.read";

    /// <summary>
    /// Kiểm tra quyền quản lý tài sản (Asset management):
    /// - Human có permission inventory.asset.manage và sở hữu tài sản (assetOwnerUserId == sub).
    /// - Human Admin (r=adm) được phép xuyên user.
    /// - Service bị từ chối.
    /// - Thiếu assetOwnerUserId phải fail closed.
    /// </summary>
    public static bool CanManageAsset(IInventoryCallerContext caller, Guid? assetOwnerUserId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || caller.IsService)
        {
            return false;
        }

        if (assetOwnerUserId is null || assetOwnerUserId == Guid.Empty)
        {
            return false;
        }

        if (caller.IsAdmin)
        {
            return true;
        }

        return caller.Permissions.Contains(PermissionAssetManage) && caller.UserId == assetOwnerUserId;
    }

    /// <summary>
    /// Kiểm tra quyền cập nhật giá thuê niêm yết của tài sản (Update Rental Rate):
    /// - Chỉ Human Owner (JWT sub == assetOwnerUserId) hoặc Human Admin (r=adm) được phép.
    /// - Không yêu cầu permission 'inventory.asset.manage'.
    /// - Service principal và người dùng khác bị từ chối.
    /// - Thiếu caller.UserId hoặc assetOwnerUserId phải fail closed.
    /// </summary>
    public static bool CanUpdateRentalRate(IInventoryCallerContext caller, Guid? assetOwnerUserId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || caller.IsService)
        {
            return false;
        }

        if (assetOwnerUserId is null || assetOwnerUserId == Guid.Empty)
        {
            return false;
        }

        if (caller.IsAdmin)
        {
            return true;
        }

        return caller.UserId.HasValue && caller.UserId.Value == assetOwnerUserId.Value;
    }

    /// <summary>
    /// Kiểm tra quyền truy cập lịch của tài sản (Calendar access cho đọc/tạo/xóa block theo DPR-149):
    /// - Chỉ Human có JWT sub == asset.OwnerUserId được phép.
    /// - Không yêu cầu permission 'p'.
    /// - Admin không được bypass ownership.
    /// - Service principal bị từ chối.
    /// - Thiếu caller.UserId hoặc assetOwnerUserId phải fail closed.
    /// </summary>
    public static bool CanAccessCalendar(IInventoryCallerContext caller, Guid? assetOwnerUserId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || caller.IsService)
        {
            return false;
        }

        if (caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return false;
        }

        if (assetOwnerUserId is null || assetOwnerUserId == Guid.Empty)
        {
            return false;
        }

        return caller.UserId == assetOwnerUserId;
    }

    /// <summary>
    /// Kiểm tra quyền xem lịch khả dụng (Availability Read):
    /// - Bất kỳ Human đã xác thực (kể cả Admin).
    /// - Service có scope inventory.availability.read.
    /// - inventory.calendar.read hoặc inventory.allocation.write riêng lẻ không cấp quyền.
    /// </summary>
    public static bool CanReadAvailability(IInventoryCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return false;
        }

        if (caller.IsService)
        {
            return caller.Scopes.Contains(ScopeAvailabilityRead);
        }

        // Bất kỳ human nào đã xác thực
        return true;
    }

    /// <summary>
    /// Kiểm tra quyền gọi các lệnh phân bổ/hold (Hold/Allocation commands):
    /// - Service có scope inventory.allocation.write.
    /// - Human (kể cả Admin) bị từ chối hoàn toàn.
    /// </summary>
    public static bool CanWriteAllocations(IInventoryCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || !caller.IsService)
        {
            return false;
        }

        return caller.Scopes.Contains(ScopeAllocationWrite);
    }

    /// <summary>
    /// Kiểm tra quyền tạo phân bổ thủ công (Manual allocation):
    /// - Human có permission inventory.asset.manage và sở hữu tài sản (assetOwnerUserId == sub).
    /// - Human Admin (r=adm) được phép xuyên user.
    /// - Service bị từ chối.
    /// - Thiếu assetOwnerUserId phải fail closed.
    /// </summary>
    public static bool CanManualAllocate(IInventoryCallerContext caller, Guid? assetOwnerUserId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated || caller.IsService)
        {
            return false;
        }

        if (assetOwnerUserId is null || assetOwnerUserId == Guid.Empty)
        {
            return false;
        }

        if (caller.IsAdmin)
        {
            return true;
        }

        return caller.Permissions.Contains(PermissionAssetManage) && caller.UserId == assetOwnerUserId;
    }
}
