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
    public const string RequirementAcceptingUnit = "Ít nhất một máy vật lý sẵn sàng nhận lịch";
    public const string RequirementOrganizationApproved = "Tổ chức đã được Platform duyệt";

    /// <summary>Mã lỗi để frontend làm mới token một lần rồi thử lại (người vừa được duyệt có thể còn token cũ).</summary>
    public const string OrganizationNotApprovedCode = "ORGANIZATION_NOT_APPROVED";

    /// <summary>
    /// Điều kiện tổ chức đạt khi người gọi có organizationId trong JWT, hoặc listing đã Active
    /// (không đổi hành vi với listing đã được công khai trước đó).
    /// </summary>
    public static bool IsOrganizationApproved(EquipmentListing listing, Guid? callerOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return callerOrganizationId.HasValue || listing.Status == Common.Enums.EquipmentListingStatus.Active;
    }

    /// <summary>
    /// Đánh giá danh sách các điều kiện còn thiếu để Listing có thể chuyển từ Draft sang Active.
    /// </summary>
    /// <param name="organizationApproved">Người gọi có organizationId trong JWT (tổ chức của chủ listing đã được duyệt).</param>
    public static IReadOnlyList<string> GetMissingRequirements(
        EquipmentListing listing,
        bool hasUnitAcceptingBookings,
        bool organizationApproved)
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

        // 5. Đơn vị máy nhận lịch
        if (!hasUnitAcceptingBookings)
        {
            missing.Add(RequirementAcceptingUnit);
        }

        // 6. Tổ chức đã duyệt
        if (!organizationApproved)
        {
            missing.Add(RequirementOrganizationApproved);
        }

        return missing;
    }

    /// <summary>
    /// Kiểm tra xem Listing có đủ điều kiện để chuyển sang trạng thái Active hay không.
    /// </summary>
    public static Result CanPublish(
        EquipmentListing listing,
        bool hasUnitAcceptingBookings,
        bool organizationApproved)
    {
        ArgumentNullException.ThrowIfNull(listing);

        if (!hasUnitAcceptingBookings)
        {
            return Error.Validation("LISTING_NEEDS_ACTIVE_UNIT", "Listing cần ít nhất một thiết bị nhận đặt lịch để chuyển sang trạng thái Active.");
        }

        // Điều kiện tổ chức kiểm tra sau cùng và có mã lỗi riêng; các điều kiện thông tin máy được đánh giá như cũ.
        var missing = GetMissingRequirements(listing, hasUnitAcceptingBookings, organizationApproved: true);
        if (missing.Count > 0)
        {
            return Error.Validation("LISTING_NOT_ELIGIBLE_FOR_PUBLISH", $"Không thể xuất bản Listing vì còn thiếu các điều kiện: {string.Join(", ", missing)}.");
        }

        if (!organizationApproved)
        {
            return Error.Conflict(OrganizationNotApprovedCode, "Tổ chức chưa được Platform duyệt nên chưa thể mở thuê Listing. Nếu vừa được duyệt, hãy làm mới phiên đăng nhập rồi thử lại.");
        }

        return Result.Success();
    }
}
