using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.Locations;

namespace Dprental.RentalService.Application.Services.Checkout;

public enum ZoneMatchStatus
{
    Success,
    SupplierNotConfigured,
    AddressIncomplete,
    OutsideZone
}

public sealed record ZoneMatchResult(ZoneMatchStatus Status, DeliveryZoneDto? Zone = null);

public static class DeliveryZoneMatcher
{
    public static ZoneMatchResult Match(
        IReadOnlyList<DeliveryZoneDto>? zones,
        string? provinceCode,
        string? wardCode,
        IVietnamDivisionsCatalog catalog)
    {
        // 1. Phân biệt: 'supplier chưa cấu hình giao hàng'
        if (zones is null || zones.Count == 0)
        {
            return new(ZoneMatchStatus.SupplierNotConfigured);
        }

        // 2. Phân biệt: 'địa chỉ thiếu thông tin'
        if (string.IsNullOrWhiteSpace(provinceCode) || !catalog.IsValidProvince(provinceCode.Trim(), out _))
        {
            return new(ZoneMatchStatus.AddressIncomplete);
        }

        string normalizedProvinceCode = provinceCode.Trim();
        string? normalizedWardCode = string.IsNullOrWhiteSpace(wardCode) ? null : wardCode.Trim();

        if (normalizedWardCode is not null && !catalog.IsValidPair(normalizedProvinceCode, normalizedWardCode, out _, out _))
        {
            return new(ZoneMatchStatus.AddressIncomplete);
        }

        // 3. Matching theo mã chuẩn (không so khớp chuỗi con hoặc đoán phường)
        var zonesInProvince = zones
            .Where(z => !string.IsNullOrWhiteSpace(z.ProvinceCode) &&
                        (string.Equals(z.ProvinceCode.Trim(), normalizedProvinceCode, StringComparison.OrdinalIgnoreCase) ||
                         (catalog.TryResolveProvince(z.ProvinceCode, out var resolvedZoneCode, out _) &&
                          string.Equals(resolvedZoneCode, normalizedProvinceCode, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        if (zonesInProvince.Count == 0)
        {
            // Supplier có cấu hình giao hàng nhưng không phục vụ tỉnh này -> Ngoài vùng
            return new(ZoneMatchStatus.OutsideZone);
        }

        // Vùng có ward cụ thể được ưu tiên hơn vùng toàn tỉnh
        if (normalizedWardCode is not null)
        {
            var specificWardZone = zonesInProvince.FirstOrDefault(z =>
                !string.IsNullOrWhiteSpace(z.WardCode) &&
                string.Equals(z.WardCode.Trim(), normalizedWardCode, StringComparison.OrdinalIgnoreCase));

            if (specificWardZone is not null)
            {
                return new(ZoneMatchStatus.Success, specificWardZone);
            }
        }

        // Tìm vùng toàn tỉnh (WardCode rỗng hoặc null)
        var wholeProvinceZone = zonesInProvince.FirstOrDefault(z => string.IsNullOrWhiteSpace(z.WardCode));
        if (wholeProvinceZone is not null)
        {
            return new(ZoneMatchStatus.Success, wholeProvinceZone);
        }

        // Nếu nhà cung cấp chỉ cấu hình theo ward cụ thể trong tỉnh này mà địa chỉ không có ward -> thiếu thông tin
        if (normalizedWardCode is null && zonesInProvince.Any(z => !string.IsNullOrWhiteSpace(z.WardCode)))
        {
            return new(ZoneMatchStatus.AddressIncomplete);
        }

        // Không khớp bất kỳ cấu hình nào -> Ngoài vùng
        return new(ZoneMatchStatus.OutsideZone);
    }
}
