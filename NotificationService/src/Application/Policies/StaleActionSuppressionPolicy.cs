using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Application.Policies;

public sealed record StaleEvaluationResult(
    bool IsStale,
    bool SuppressAction,
    bool SuppressEmail,
    string? Reason);

public static class StaleActionSuppressionPolicy
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXPIRED",
        "CANCELLED",
        "COMPLETED",
        "REJECTED"
    };

    /// <summary>
    /// Đánh giá xem sự kiện hoặc thông báo có bị coi là lỗi thời dựa trên watermark của aggregate không.
    /// </summary>
    public static StaleEvaluationResult Evaluate(
        string notificationType,
        long incomingOrderVersion,
        NotificationAggregateWatermark? watermark)
    {
        if (watermark == null)
        {
            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        // Trường hợp 1: Order đã ở trạng thái terminal (EXPIRED, CANCELLED, COMPLETED)
        if (watermark.IsTerminal && !string.IsNullOrWhiteSpace(watermark.TerminalState) && TerminalStates.Contains(watermark.TerminalState))
        {
            var terminal = watermark.TerminalState.ToUpperInvariant();

            // Đơn terminal chặn toàn bộ hành động của các thông báo thuộc đơn hàng hoặc thiết bị dưới đơn hàng
            if (notificationType is "ORDER_CREATED" or "SUPPLIER_CONFIRMATION_REQUIRED" or "HANDOVER_OVERDUE_SUPPLIER" or "RETURN_CONFIRMATION_OVERDUE_SUPPLIER")
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"Đơn thuê đã ở trạng thái kết thúc ({terminal}).");
            }
        }

        // Trường hợp 2: Milestone PAID (đã thanh toán)
        // Ghi nhận mốc thanh toán; chặn CTA thanh toán của ORDER_CREATED nhưng KHÔNG chặn xác nhận của nhà cung cấp và KHÔNG coi là terminal
        if (watermark.IsPaid || string.Equals(watermark.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase))
        {
            if (notificationType is "ORDER_CREATED")
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: "Đơn thuê đã hoàn tất thanh toán (mốc PAID).");
            }
        }

        // Trường hợp 3: incomingOrderVersion nhỏ hơn phiên bản watermark hiện tại
        if (incomingOrderVersion > 0 && incomingOrderVersion < watermark.LastOrderVersion)
        {
            var suppressAction = notificationType is "ORDER_CREATED" or "SUPPLIER_CONFIRMATION_REQUIRED" or "HANDOVER_OVERDUE_SUPPLIER" or "RETURN_CONFIRMATION_OVERDUE_SUPPLIER";
            return new StaleEvaluationResult(
                IsStale: true,
                SuppressAction: suppressAction,
                SuppressEmail: suppressAction,
                Reason: $"Phiên bản sự kiện ({incomingOrderVersion}) nhỏ hơn phiên bản hiện tại ({watermark.LastOrderVersion}).");
        }

        return new StaleEvaluationResult(
            IsStale: false,
            SuppressAction: false,
            SuppressEmail: false,
            Reason: null);
    }
}
