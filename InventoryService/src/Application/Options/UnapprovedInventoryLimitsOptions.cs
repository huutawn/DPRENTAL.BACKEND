namespace Dprental.InventoryService.Application.Options;

/// <summary>
/// Hạn mức bản nháp cho tài khoản chưa có tổ chức được duyệt (JWT không có organizationId).
/// Hạn mức thường không tính máy ngừng hoạt động và listing Inactive; trần tuyệt đối tính tất cả để
/// việc tắt nhận đặt không mở thêm chỗ tạo mới vô hạn.
/// </summary>
public sealed class UnapprovedInventoryLimitsOptions
{
    public const string SectionName = "Inventory:UnapprovedLimits";

    public int MaxListings { get; init; } = 5;

    public int MaxAssets { get; init; } = 30;

    public int MaxListingsHardCap { get; init; } = 10;

    public int MaxAssetsHardCap { get; init; } = 60;

    public bool IsValid() =>
        MaxListings > 0
        && MaxAssets > 0
        && MaxListingsHardCap >= MaxListings
        && MaxAssetsHardCap >= MaxAssets;
}
