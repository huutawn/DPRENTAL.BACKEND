namespace Dprental.InventoryService.Application.Security;

/// <summary>
/// Bộ đánh giá quyền truy cập tập trung của Inventory Service (Access Evaluator).
/// Thực thi ma trận phân quyền DPR-147 dựa trên User Ownership (OwnerUserId) và Claims.
/// </summary>
public static class InventoryAccessEvaluator
{
    public const string PermissionAssetManage = "inventory.asset.manage";
    public const string PermissionCalendarRead = "inventory.calendar.read";
    public const string ScopeAllocationWrite = "inventory.allocation.write";
    public const string ScopeCalendarRead = "inventory.calendar.read";

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
    /// Kiểm tra quyền xem chi tiết lịch của tài sản (Calendar detail):
    /// - Human có permission inventory.calendar.read và sở hữu tài sản (assetOwnerUserId == sub).
    /// - Human Admin (r=adm) được phép xuyên user.
    /// - Service bị từ chối.
    /// - Thiếu assetOwnerUserId phải fail closed.
    /// </summary>
    public static bool CanReadCalendarDetail(IInventoryCallerContext caller, Guid? assetOwnerUserId)
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

        return caller.Permissions.Contains(PermissionCalendarRead) && caller.UserId == assetOwnerUserId;
    }

    /// <summary>
    /// Kiểm tra quyền xem lịch khả dụng đã làm sạch (Sanitized availability):
    /// - Bất kỳ Human đã xác thực (kể cả Admin).
    /// - Service có scope inventory.calendar.read.
    /// </summary>
    public static bool CanReadSanitizedAvailability(IInventoryCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return false;
        }

        if (caller.IsService)
        {
            return caller.Scopes.Contains(ScopeCalendarRead);
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
