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
        "PAID",
        "EXPIRED",
        "CANCELLED",
        "COMPLETED"
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

        // Trường hợp 1: Order đã ở trạng thái terminal
        if (watermark.IsTerminal && !string.IsNullOrWhiteSpace(watermark.TerminalState))
        {
            var terminal = watermark.TerminalState.ToUpperInvariant();

            // Nếu đơn đã thanh toán, hủy, hoặc hết hạn thì các hành động thanh toán/xác nhận phải bị chặn
            if (notificationType is "ORDER_CREATED" or "SUPPLIER_CONFIRMATION_REQUIRED")
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"Đơn thuê đã ở trạng thái kết thúc ({terminal}).");
            }

            // Nếu đơn đã hoàn tất hoặc hủy thì cảnh báo quá hạn bàn giao/hoàn trả không còn hành động
            if (notificationType is "HANDOVER_OVERDUE_SUPPLIER" or "RETURN_CONFIRMATION_OVERDUE_SUPPLIER")
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"Đơn thuê đã ở trạng thái kết thúc ({terminal}).");
            }
        }

        // Trường hợp 2: incomingOrderVersion nhỏ hơn phiên bản watermark hiện tại
        if (incomingOrderVersion > 0 && incomingOrderVersion < watermark.LastOrderVersion)
        {
            var suppressAction = notificationType is "ORDER_CREATED" or "SUPPLIER_CONFIRMATION_REQUIRED";
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
