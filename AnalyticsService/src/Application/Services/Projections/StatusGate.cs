using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Application.Services.Projections;

/// <summary>
/// Cổng version cho cột Status: chặn event cũ kéo trạng thái lùi, nhưng vẫn cho các event
/// cùng transaction (cùng orderVersion) cập nhật nếu trạng thái mới "xa hơn".
/// Chỉ áp dụng cho Status; cột thời gian được điền một lần ở projector, không qua cổng này.
/// </summary>
public static class StatusGate
{
    public static void Apply(RentalOrderFact order, OrderFactStatus status, long? eventVersion)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (CanApply(order.LastVersion, Rank(order.Status), eventVersion, Rank(status)))
        {
            order.Status = status;
            order.LastVersion = Max(order.LastVersion, eventVersion);
        }
    }

    public static void Apply(RentalItemFact item, ItemFactStatus status, long? eventVersion)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (CanApply(item.LastOrderVersion, Rank(item.Status), eventVersion, Rank(status)))
        {
            item.Status = status;
            item.LastOrderVersion = Max(item.LastOrderVersion, eventVersion);
        }
    }

    /// <summary>Quy tắc cổng version (xem bảng trong phần lý thuyết B4).</summary>
    public static bool CanApply(long? lastVersion, int currentRank, long? eventVersion, int newRank)
    {
        // Event lịch sử không có version: không biết cũ hay mới. Chỉ cho tiến lên (không bao giờ lùi),
        // và chỉ khi dòng cũng chưa từng nhận event có version.
        if (eventVersion is null)
        {
            return lastVersion is null && newRank > currentRank;
        }

        if (lastVersion is null || eventVersion > lastVersion)
        {
            return true;
        }

        // Cùng version = cùng transaction bên Rental: trạng thái "xa hơn" thắng
        if (eventVersion == lastVersion)
        {
            return newRank > currentRank;
        }

        return false; // Event cũ đến muộn: không rollback
    }

    public static int Rank(OrderFactStatus status) => status switch
    {
        OrderFactStatus.PendingPayment => 1,
        OrderFactStatus.Paid => 2,
        _ => 3 // Completed, Cancelled, Expired, Rejected: trạng thái kết thúc
    };

    public static int Rank(ItemFactStatus status) => status switch
    {
        ItemFactStatus.PendingPayment => 1,
        ItemFactStatus.PendingSupplierConfirmation => 2,
        ItemFactStatus.AwaitingHandover => 3,
        ItemFactStatus.InRental => 4,
        ItemFactStatus.PendingSettlement => 5,
        _ => 6 // Completed, Rejected, Expired, Cancelled: trạng thái kết thúc
    };

    private static long? Max(long? current, long? incoming) =>
        incoming is null ? current : current is null ? incoming : Math.Max(current.Value, incoming.Value);
}
