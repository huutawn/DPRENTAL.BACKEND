using Dprental.AnalyticsService.Domain.Entities;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Cổng version cho cột Status. Cột thời gian KHÔNG đi qua cổng này (chúng được điền một lần ở projector).
/// StatusVersion = 0 nghĩa là chưa nhận event nào có version (tương đương null), nên không cần migration.
/// </summary>
public static class StatusGate
{
    public static void Apply(RentalOrderFact order, string status, long? eventVersion)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (CanApply(order.StatusVersion, OrderRank(order.Status), eventVersion, OrderRank(status)))
        {
            order.Status = status;
            order.StatusVersion = Max(order.StatusVersion, eventVersion);
        }
    }

    public static void Apply(RentalItemFact item, string status, long? eventVersion)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (CanApply(item.StatusVersion, ItemRank(item.Status), eventVersion, ItemRank(status)))
        {
            item.Status = status;
            item.StatusVersion = Max(item.StatusVersion, eventVersion);
        }
    }

    public static bool CanApply(long lastVersion, int currentRank, long? eventVersion, int newRank)
    {
        // Event lịch sử không có version: không biết cũ hay mới → chỉ cho tiến lên, không bao giờ lùi,
        // và chỉ khi dòng chưa từng nhận event có version.
        if (eventVersion is null or <= 0)
        {
            return lastVersion <= 0 && newRank > currentRank;
        }

        if (lastVersion <= 0 || eventVersion > lastVersion)
        {
            return true;
        }

        // Cùng version = cùng transaction bên Rental: trạng thái "xa hơn" thắng, không bỏ event
        if (eventVersion == lastVersion)
        {
            return newRank > currentRank;
        }

        return false; // Event cũ đến muộn: không rollback
    }

    // Giá trị lạ (dữ liệu cũ, chuỗi rỗng) xếp hạng 0 như UNKNOWN: luôn được cập nhật tiếp,
    // không bao giờ bị "khóa" như trạng thái kết thúc.
    public static int OrderRank(string status) => status switch
    {
        OrderStatuses.PendingPayment => 1,
        OrderStatuses.Paid => 2,
        OrderStatuses.Completed or OrderStatuses.Cancelled or OrderStatuses.Expired or OrderStatuses.Rejected => 3,
        _ => 0
    };

    public static int ItemRank(string status) => status switch
    {
        ItemStatuses.PendingPayment => 1,
        ItemStatuses.PendingSupplierConfirmation => 2,
        ItemStatuses.AwaitingHandover => 3,
        ItemStatuses.InRental => 4,
        ItemStatuses.PendingSettlement => 5,
        ItemStatuses.Completed or ItemStatuses.Rejected or ItemStatuses.Expired or ItemStatuses.Cancelled => 6,
        _ => 0
    };

    private static long Max(long current, long? incoming) =>
        incoming is > 0 ? Math.Max(current, incoming.Value) : current;
}
