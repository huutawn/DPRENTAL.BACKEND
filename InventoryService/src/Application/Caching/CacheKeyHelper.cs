using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Caching;

/// <summary>
/// Tiện ích hỗ trợ chuẩn hóa hash input và tính toán deadline cho Valkey Read Cache.
/// Đảm bảo serialization có cấu trúc, không va chạm delimiter và giữ nguyên semantics.
/// </summary>
public static class CacheKeyHelper
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// <summary>
    /// Băm chuỗi input đã chuẩn hóa thành chuỗi hex SHA256 ngắn gọn.
    /// </summary>
    public static string ComputeSha256(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "empty";
        }

        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Tạo hash có cấu trúc cho tìm kiếm danh mục công khai.
    /// </summary>
    public static string ComputePublicSearchHash(
        int page,
        int pageSize,
        string? q,
        Guid? equipmentTypeId,
        Guid? brandId,
        Guid? categoryId,
        string? provinceCode,
        string? wardCode,
        string? sort,
        string? filters,
        DateOnly? from,
        DateOnly? to,
        int? requestedQuantity,
        DateOnly todayVn)
    {
        var descriptor = new
        {
            page,
            pageSize,
            q = q?.Trim(),
            equipmentTypeId,
            brandId,
            categoryId,
            provinceCode = provinceCode?.Trim().ToUpperInvariant(),
            wardCode = wardCode?.Trim().ToUpperInvariant(),
            sort = sort?.Trim().ToLowerInvariant(),
            filters = filters?.Trim(),
            from = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            requestedQuantity,
            todayVn = todayVn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        return ComputeSha256(JsonSerializer.Serialize(descriptor));
    }

    /// <summary>
    /// Tạo hash có cấu trúc cho tìm kiếm listing của owner.
    /// </summary>
    public static string ComputeOwnerListingSearchHash(
        int page,
        int pageSize,
        string? q,
        Guid? equipmentTypeId,
        Guid? brandId,
        EquipmentListingStatus? status,
        string? sort)
    {
        var descriptor = new
        {
            page,
            pageSize,
            q = q?.Trim(),
            equipmentTypeId,
            brandId,
            status = status?.ToString(),
            sort = sort?.Trim().ToLowerInvariant()
        };

        return ComputeSha256(JsonSerializer.Serialize(descriptor));
    }

    /// <summary>
    /// Tạo hash có cấu trúc cho tìm kiếm asset của owner.
    /// </summary>
    public static string ComputeOwnerAssetSearchHash(
        int page,
        int pageSize,
        string? q,
        Guid? equipmentTypeId,
        Guid? brandId,
        int? manufactureYearFrom,
        int? manufactureYearTo,
        bool? acceptingBookings,
        string? sort,
        string? filters)
    {
        var descriptor = new
        {
            page,
            pageSize,
            q = q?.Trim(),
            equipmentTypeId,
            brandId,
            manufactureYearFrom,
            manufactureYearTo,
            acceptingBookings,
            sort = sort?.Trim().ToLowerInvariant(),
            filters = filters?.Trim()
        };

        return ComputeSha256(JsonSerializer.Serialize(descriptor));
    }

    /// <summary>
    /// Tạo hash có cấu trúc cho cửa sổ ngày và tham số số lượng (dùng cho calendar/availability check).
    /// </summary>
    public static string ComputeWindowHash(DateOnly start, DateOnly end, int? quantity = null)
    {
        var descriptor = new
        {
            start = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            quantity
        };

        return ComputeSha256(JsonSerializer.Serialize(descriptor));
    }

    /// <summary>
    /// Tính toán deadline tuyệt đối cho occupancy:
    /// min(normal TTL, nearest hold expiry, thời điểm quy tắc occupancy thay đổi: chuyển ngày VN hoặc chuyển sang overdue).
    /// Bảo vệ phép cộng ngày tại DateOnly.MaxValue.
    /// </summary>
    public static DateTimeOffset CalculateOccupancyDeadline(
        DateTimeOffset normalDeadline,
        DateTimeOffset? nearestHoldExpiry,
        IEnumerable<AssetAllocation>? allocations,
        DateTimeOffset nowUtc)
    {
        var deadline = normalDeadline;

        // 1. Nearest active Hold expiry
        if (nearestHoldExpiry.HasValue && nearestHoldExpiry.Value < deadline && nearestHoldExpiry.Value > nowUtc)
        {
            deadline = nearestHoldExpiry.Value;
        }

        // 2. Chuyển ngày Việt Nam (nửa đêm UTC+7 tiếp theo)
        var vnNow = nowUtc.ToOffset(VietnamOffset);
        var nextVnMidnight = new DateTimeOffset(vnNow.Year, vnNow.Month, vnNow.Day, 0, 0, 0, VietnamOffset)
            .AddDays(1)
            .ToUniversalTime();

        if (nextVnMidnight < deadline && nextVnMidnight > nowUtc)
        {
            deadline = nextVnMidnight;
        }

        // 3. Chuyển sang overdue: allocation đang HandedOver nhưng chưa Returned, ngày VN vượt EndDate
        if (allocations != null)
        {
            foreach (var alloc in allocations)
            {
                if (alloc.Status == AllocationStatus.Allocated &&
                    alloc.HandedOverAt.HasValue &&
                    !alloc.ReturnedAt.HasValue &&
                    alloc.RentalPeriod.EndDate < DateOnly.MaxValue)
                {
                    var overdueTransitionUtc = new DateTimeOffset(
                        alloc.RentalPeriod.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
                        VietnamOffset).ToUniversalTime();

                    if (overdueTransitionUtc > nowUtc && overdueTransitionUtc < deadline)
                    {
                        deadline = overdueTransitionUtc;
                    }
                }
            }
        }

        return deadline;
    }
}
