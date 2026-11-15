using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Policies;

/// <summary>
/// Chính sách hợp nhất đánh giá điều kiện xuất bản (Mở thuê - chuyển sang Active) cho một EquipmentListing.
/// Đảm bảo tính nhất quán giữa kiểm tra khi Publish và danh sách MissingRequirements trả về cho API/Frontend.
/// </summary>
public static class EquipmentListingPublishPolicy
{
    public const string RequirementName = "Tên thiết bị";
    public const string RequirementModel = "Model thiết bị";
    public const string RequirementPhotos = "Hình ảnh thiết bị (tối thiểu 1 ảnh)";
    public const string RequirementThumbnail = "Ảnh đại diện (Thumbnail)";
    public const string RequirementDailyRate = "Giá thuê theo ngày";
    public const string RequirementWarehouseAddress = "Địa chỉ kho hàng hợp lệ";
    public const string RequirementAcceptingUnit = "Ít nhất một máy vật lý sẵn sàng nhận lịch (AcceptingBookings)";

    /// <summary>
    /// Đánh giá danh sách các điều kiện còn thiếu để Listing có thể chuyển từ Draft sang Active.
    /// </summary>
    public static IReadOnlyList<string> GetMissingRequirements(
        EquipmentListing listing,
        bool hasValidWarehouseAddress,
        bool hasUnitAcceptingBookings)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var missing = new List<string>();

        // 1. Thông tin cơ bản máy
        if (string.IsNullOrWhiteSpace(listing.Name))
        {
            missing.Add(RequirementName);
        }

        if (string.IsNullOrWhiteSpace(listing.Model))
        {
            missing.Add(RequirementModel);
        }

        // 2. Hình ảnh hợp lệ
        if (listing.MediaIds is null || listing.MediaIds.Length == 0)
        {
            missing.Add(RequirementPhotos);
        }

        if (!listing.ThumbnailMediaId.HasValue || listing.ThumbnailMediaId.Value == Guid.Empty)
        {
            missing.Add(RequirementThumbnail);
        }

        // 3. Giá & cọc
        if (listing.DailyRate < EquipmentListing.MinRate)
        {
            missing.Add(RequirementDailyRate);
        }

        // 4. Kho hàng
        if (!hasValidWarehouseAddress)
        {
            missing.Add(RequirementWarehouseAddress);
        }

        // 5. Đơn vị máy nhận lịch
        if (!hasUnitAcceptingBookings)
        {
            missing.Add(RequirementAcceptingUnit);
        }

        return missing;
    }

    /// <summary>
    /// Kiểm tra xem Listing có đủ điều kiện để chuyển sang trạng thái Active hay không.
    /// </summary>
    public static Result CanPublish(
        EquipmentListing listing,
        bool hasValidWarehouseAddress,
        bool hasUnitAcceptingBookings)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (!hasValidWarehouseAddress)
        {
            return Error.Validation("WAREHOUSE_ADDRESS_REQUIRED", "Cần thiết lập địa chỉ kho hợp lệ trước khi chuyển Listing sang trạng thái Active.");
        }

        if (!hasUnitAcceptingBookings)
        {
            return Error.Validation("LISTING_NEEDS_ACTIVE_UNIT", "Listing cần ít nhất một thiết bị nhận đặt lịch để chuyển sang trạng thái Active.");
        }

        var missing = GetMissingRequirements(listing, hasValidWarehouseAddress, hasUnitAcceptingBookings);
        if (missing.Count > 0)
        {
            return Error.Validation("LISTING_NOT_ELIGIBLE_FOR_PUBLISH", $"Không thể xuất bản Listing vì còn thiếu các điều kiện: {string.Join(", ", missing)}.");
        }

        return Result.Success();
    }
}
