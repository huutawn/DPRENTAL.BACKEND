using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Common;

namespace Dprental.InventoryService.Application.Policies;

/// <summary>
/// Chính sách hạn mức bản nháp cho người chưa có tổ chức được duyệt. Chỉ quyết định từ các số đếm đã có;
/// việc đếm và khóa chủ sở hữu thuộc Application Service.
/// </summary>
public static class UnapprovedInventoryLimitPolicy
{
    public const string ListingLimitCode = "UNAPPROVED_LISTING_LIMIT_EXCEEDED";
    public const string AssetLimitCode = "UNAPPROVED_ASSET_LIMIT_EXCEEDED";

    /// <summary>Hạn mức áp dụng cho người gọi không phải Admin và chưa có organizationId trong JWT.</summary>
    public static bool Applies(bool isAdmin, Guid? organizationId) => !isAdmin && !organizationId.HasValue;

    /// <summary>Kiểm tra trước khi tạo thêm <paramref name="adding"/> listing.</summary>
    /// <param name="countedListings">Listing của chủ, không tính Inactive.</param>
    /// <param name="allListings">Mọi listing của chủ.</param>
    public static Result CheckListings(int countedListings, int allListings, int adding, UnapprovedInventoryLimitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (countedListings + adding > options.MaxListings || allListings + adding > options.MaxListingsHardCap)
        {
            return Error.Conflict(
                ListingLimitCode,
                $"Tài khoản chưa được duyệt chỉ tạo tối đa {options.MaxListings} listing (tối đa {options.MaxListingsHardCap} kể cả listing ngừng hoạt động). Hãy chờ tổ chức được duyệt.");
        }

        return Result.Success();
    }

    /// <summary>Kiểm tra trước khi tạo thêm <paramref name="adding"/> máy.</summary>
    /// <param name="countedAssets">Máy của chủ, không tính máy ngừng nhận đặt.</param>
    /// <param name="allAssets">Mọi máy của chủ.</param>
    public static Result CheckAssets(int countedAssets, int allAssets, int adding, UnapprovedInventoryLimitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (countedAssets + adding > options.MaxAssets || allAssets + adding > options.MaxAssetsHardCap)
        {
            return Error.Conflict(
                AssetLimitCode,
                $"Tài khoản chưa được duyệt chỉ tạo tối đa {options.MaxAssets} máy (tối đa {options.MaxAssetsHardCap} kể cả máy ngừng hoạt động). Hãy chờ tổ chức được duyệt.");
        }

        return Result.Success();
    }
}
