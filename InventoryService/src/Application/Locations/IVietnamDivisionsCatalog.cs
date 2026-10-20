namespace Dprental.InventoryService.Application.Locations;

public sealed record WardDivision(string Code, string Name);

public sealed record ProvinceDivision(string Code, string Name, IReadOnlyList<WardDivision> Wards);

/// <summary>
/// Danh mục phân cấp hành chính chuẩn (Tỉnh/Thành phố, Phường/Xã) của Việt Nam.
/// Đồng bộ với frontend theo chuẩn dữ liệu vietnam-divisions.json.
/// </summary>
public interface IVietnamDivisionsCatalog
{
    /// <summary>
    /// Kiểm tra tính hợp lệ của cặp (Mã tỉnh, Mã phường).
    /// Nếu hợp lệ, trả về true kèm tên chuẩn của tỉnh và phường.
    /// </summary>
    bool IsValidPair(string? provinceCode, string? wardCode, out string canonicalProvinceName, out string canonicalWardName);

    /// <summary>
    /// Kiểm tra tính hợp lệ của mã tỉnh.
    /// Nếu hợp lệ, trả về true kèm tên chuẩn của tỉnh.
    /// </summary>
    bool IsValidProvince(string? provinceCode, out string canonicalProvinceName);

    /// <summary>
    /// Lấy danh sách toàn bộ tỉnh/thành phố và các phường/xã trực thuộc.
    /// </summary>
    IReadOnlyList<ProvinceDivision> GetAllProvinces();
}
