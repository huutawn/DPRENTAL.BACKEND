namespace Dprental.RentalService.Application.Policies;

using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

/// <summary>
/// Policy deterministic đánh giá và tính toán trạng thái rollup của RentalOrder dựa trên các RentalItem.
/// </summary>
public static class RentalOrderStatusPolicy
{
    private static readonly RentalOrderStatus[] TerminalStatuses =
    [
        RentalOrderStatus.Completed,
        RentalOrderStatus.Cancelled,
        RentalOrderStatus.Expired,
        RentalOrderStatus.Rejected
    ];

    /// <summary>
    /// Đánh giá trạng thái rollup và áp dụng vào RentalOrder nếu trạng thái thực sự thay đổi.
    /// </summary>
    /// <param name="order">Đơn hàng cần rollup.</param>
    /// <param name="utcNow">Thời điểm đánh giá.</param>
    /// <param name="operationId">Mã thao tác gây ra thay đổi.</param>
    /// <returns>True nếu trạng thái thực sự thay đổi; False nếu không đổi.</returns>
    public static bool Apply(RentalOrder order, DateTimeOffset utcNow, string operationId)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Giữ nguyên Order đã terminal hoặc còn PendingPayment.
        if (order.Status == RentalOrderStatus.PendingPayment || TerminalStatuses.Contains(order.Status))
        {
            return false;
        }

        // Tập item rỗng không được suy ra Completed hay thay đổi trạng thái
        if (order.Items.Count == 0)
        {
            return false;
        }

        RentalOrderStatus? candidate = CalculateCandidateStatus(order.Items);
        if (!candidate.HasValue || candidate.Value == order.Status)
        {
            return false;
        }

        // Chặn lùi bằng thứ tự lifecycle tường minh: Paid -> AwaitingHandover -> InRental -> PendingSettlement -> Completed
        int currentRank = GetLifecycleRank(order.Status);
        int candidateRank = GetLifecycleRank(candidate.Value);

        if (candidateRank > 0)
        {
            // Cả hai đều thuộc lifecycle: chỉ cho phép tiến về phía trước (cho phép tiến thẳng nếu item đi xa hơn)
            if (candidateRank <= currentRank)
            {
                return false;
            }
        }
        else
        {
            // Candidate là terminal: Cancelled, Expired, Rejected
            // Từ Paid có thể sang Cancelled, Expired, Rejected khi không còn active item
            // Expired cũng hợp lệ khi mọi item AwaitingHandover đều quá hạn giao; Rejected chỉ từ Paid.
            if ((candidate.Value == RentalOrderStatus.Rejected && order.Status != RentalOrderStatus.Paid) ||
                (candidate.Value == RentalOrderStatus.Expired &&
                 order.Status is not (RentalOrderStatus.Paid or RentalOrderStatus.AwaitingHandover)))
            {
                return false;
            }
        }

        string reason = GetRollupReason(candidate.Value);
        order.ApplyStatusRollup(candidate.Value, utcNow, operationId, reason);
        return true;
    }

    /// <summary>
    /// Tính trạng thái ứng viên theo thứ tự ưu tiên nghiệp vụ.
    /// Active item là item có trạng thái khác Rejected, Expired, Cancelled.
    /// </summary>
    public static RentalOrderStatus? CalculateCandidateStatus(IReadOnlyCollection<RentalItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        // 1. Còn PendingSupplierConfirmation -> Paid
        if (items.Any(i => i.Status == RentalItemStatus.PendingSupplierConfirmation))
        {
            return RentalOrderStatus.Paid;
        }

        // Active item: loại bỏ Rejected, Expired, Cancelled
        var activeItems = items
            .Where(i => i.Status is not (RentalItemStatus.Rejected or RentalItemStatus.Expired or RentalItemStatus.Cancelled))
            .ToList();

        // 2. Có active item AwaitingHandover -> AwaitingHandover
        if (activeItems.Any(i => i.Status == RentalItemStatus.AwaitingHandover))
        {
            return RentalOrderStatus.AwaitingHandover;
        }

        // 3. Có active item InRental -> InRental
        if (activeItems.Any(i => i.Status == RentalItemStatus.InRental))
        {
            return RentalOrderStatus.InRental;
        }

        // 4. Có active item PendingSettlement -> PendingSettlement
        if (activeItems.Any(i => i.Status == RentalItemStatus.PendingSettlement))
        {
            return RentalOrderStatus.PendingSettlement;
        }

        // 5. Có active item và tất cả đã Completed
        // (Chú ý: Còn PendingPayment không được suy ra Completed)
        if (activeItems.Count > 0 &&
            activeItems.All(i => i.Status == RentalItemStatus.Completed) &&
            !items.Any(i => i.Status == RentalItemStatus.PendingPayment))
        {
            return RentalOrderStatus.Completed;
        }

        // Không còn active item:
        if (activeItems.Count == 0)
        {
            // 6. Không còn active item, có Cancelled -> Cancelled
            if (items.Any(i => i.Status == RentalItemStatus.Cancelled))
            {
                return RentalOrderStatus.Cancelled;
            }

            // 7. Không còn active item, có Expired -> Expired
            if (items.Any(i => i.Status == RentalItemStatus.Expired))
            {
                return RentalOrderStatus.Expired;
            }

            // 8. Tất cả item Rejected -> Rejected
            if (items.All(i => i.Status == RentalItemStatus.Rejected))
            {
                return RentalOrderStatus.Rejected;
            }
        }

        return null;
    }

    private static int GetLifecycleRank(RentalOrderStatus status) => status switch
    {
        RentalOrderStatus.Paid => 1,
        RentalOrderStatus.AwaitingHandover => 2,
        RentalOrderStatus.InRental => 3,
        RentalOrderStatus.PendingSettlement => 4,
        RentalOrderStatus.Completed => 5,
        _ => 0
    };

    private static string GetRollupReason(RentalOrderStatus status) => status switch
    {
        RentalOrderStatus.Paid => "Awaiting supplier confirmation on remaining items",
        RentalOrderStatus.AwaitingHandover => "Supplier confirmation completed with accepted items; awaiting handover",
        RentalOrderStatus.InRental => "All items handed over; rental in progress",
        RentalOrderStatus.PendingSettlement => "All items returned; awaiting settlement",
        RentalOrderStatus.Completed => "All active items completed; order completed",
        RentalOrderStatus.Cancelled => "All items cancelled",
        RentalOrderStatus.Expired => "Items ended in reject/expiry without any accepted items",
        RentalOrderStatus.Rejected => "All items rejected by supplier",
        _ => $"Order status rolled up to {status}"
    };
}
