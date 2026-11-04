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
        "REJECTED",
        "RETURNED"
    };

    private static readonly HashSet<string> ActionableNotificationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ORDER_CREATED",
        "SUPPLIER_CONFIRMATION_REQUIRED",
        "HANDOVER_OVERDUE_SUPPLIER",
        "RETURN_CONFIRMATION_OVERDUE_SUPPLIER"
    };

    /// <summary>
    /// Đánh giá xem sự kiện hoặc thông báo có bị coi là lỗi thời dựa trên trạng thái tổng hợp (aggregate state projection) không.
    /// </summary>
    public static StaleEvaluationResult Evaluate(
        string notificationType,
        long incomingOrderVersion,
        AggregateStateProjection? aggregateState)
    {
        // Financial notifications (REFUND_COMPLETED, PAYOUT_COMPLETED): NEVER apply Rental stale suppression
        if (notificationType is "REFUND_COMPLETED" or "PAYOUT_COMPLETED")
        {
            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        // Informational/result notifications: NEVER suppress ORDER_PAID, ORDER_COMPLETED
        if (notificationType is "ORDER_PAID" or "ORDER_COMPLETED")
        {
            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        if (aggregateState == null)
        {
            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        // 1. Terminal state thật: EXPIRED, CANCELLED, COMPLETED, REJECTED, RETURNED
        // PAID chỉ là milestone thanh toán, KHÔNG được coi là terminal và KHÔNG được phủ định terminal state thật
        var actualTerminalState = !string.IsNullOrWhiteSpace(aggregateState.TerminalState) &&
                                  !string.Equals(aggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase)
            ? aggregateState.TerminalState
            : null;

        var isTerminal = (actualTerminalState != null && TerminalStates.Contains(actualTerminalState)) ||
                         (aggregateState.IsTerminal && actualTerminalState != null);

        if (isTerminal)
        {
            var terminal = actualTerminalState!.ToUpperInvariant();
            var scopeName = string.Equals(aggregateState.Scope, NotificationWatermarkScope.RentalItem, StringComparison.OrdinalIgnoreCase)
                ? "Thiết bị thuê"
                : "Đơn thuê";

            // Khi ở trạng thái terminal, CHỈ chặn các thông báo có tính hành động (actionable)
            if (ActionableNotificationTypes.Contains(notificationType))
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"{scopeName} đã ở trạng thái kết thúc ({terminal}).");
            }

            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        // 2. Milestone PAID (đã thanh toán)
        // Ghi nhận mốc thanh toán; chặn CTA thanh toán của ORDER_CREATED nhưng KHÔNG chặn xác nhận của nhà cung cấp và KHÔNG coi là terminal
        var isPaidMilestone = aggregateState.IsPaid || string.Equals(aggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase);
        if (isPaidMilestone)
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

        // 3. incomingOrderVersion nhỏ hơn phiên bản watermark hiện tại trong cùng scope
        if (incomingOrderVersion > 0 && incomingOrderVersion < aggregateState.MaxVersion)
        {
            var suppressAction = ActionableNotificationTypes.Contains(notificationType);
            return new StaleEvaluationResult(
                IsStale: suppressAction,
                SuppressAction: suppressAction,
                SuppressEmail: suppressAction,
                Reason: suppressAction ? $"Phiên bản sự kiện ({incomingOrderVersion}) nhỏ hơn phiên bản hiện tại ({aggregateState.MaxVersion})." : null);
        }

        return new StaleEvaluationResult(
            IsStale: false,
            SuppressAction: false,
            SuppressEmail: false,
            Reason: null);
    }

    /// <summary>
    /// Đánh giá tính lỗi thời kết hợp giữa aggregate hiện tại (ReturnRequest / Item), parent Item và parent Order.
    /// </summary>
    public static StaleEvaluationResult EvaluateWithParentState(
        string notificationType,
        long incomingOrderVersion,
        AggregateStateProjection? currentAggregateState,
        AggregateStateProjection? parentItemAggregateState = null,
        AggregateStateProjection? parentOrderAggregateState = null)
    {
        if (notificationType is "REFUND_COMPLETED" or "PAYOUT_COMPLETED" or "ORDER_PAID" or "ORDER_COMPLETED")
        {
            return Evaluate(notificationType, incomingOrderVersion, currentAggregateState);
        }

        // 1. Đánh giá trạng thái parent order (terminal & paid) - TUYỆT ĐỐI không dùng version parent order để so sánh với version con
        if (parentOrderAggregateState != null)
        {
            var orderTerminalState = !string.IsNullOrWhiteSpace(parentOrderAggregateState.TerminalState) &&
                                     !string.Equals(parentOrderAggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase)
                ? parentOrderAggregateState.TerminalState
                : null;

            var isOrderTerminal = (orderTerminalState != null && TerminalStates.Contains(orderTerminalState)) ||
                                  (parentOrderAggregateState.IsTerminal && orderTerminalState != null);

            if (isOrderTerminal && ActionableNotificationTypes.Contains(notificationType))
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"Đơn thuê đã ở trạng thái kết thúc ({orderTerminalState!.ToUpperInvariant()}).");
            }

            var isOrderPaid = parentOrderAggregateState.IsPaid || string.Equals(parentOrderAggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase);
            if (isOrderPaid && notificationType is "ORDER_CREATED")
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: "Đơn thuê đã hoàn tất thanh toán (mốc PAID).");
            }
        }

        // 2. Đánh giá trạng thái parent item (returned / completed / terminal) - TUYỆT ĐỐI không dùng version item so sánh với ReturnRequest
        if (parentItemAggregateState != null)
        {
            var itemTerminalState = !string.IsNullOrWhiteSpace(parentItemAggregateState.TerminalState) &&
                                    !string.Equals(parentItemAggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase)
                ? parentItemAggregateState.TerminalState
                : null;

            var isItemTerminal = (itemTerminalState != null && TerminalStates.Contains(itemTerminalState)) ||
                                 (parentItemAggregateState.IsTerminal && itemTerminalState != null);

            if (isItemTerminal)
            {
                if (string.Equals(notificationType, "RETURN_CONFIRMATION_OVERDUE_SUPPLIER", StringComparison.OrdinalIgnoreCase) ||
                    ActionableNotificationTypes.Contains(notificationType))
                {
                    return new StaleEvaluationResult(
                        IsStale: true,
                        SuppressAction: true,
                        SuppressEmail: true,
                        Reason: $"Thiết bị thuê đã ở trạng thái kết thúc ({itemTerminalState!.ToUpperInvariant()}).");
                }
            }
        }

        // 3. Đánh giá trạng thái và version của chính aggregate hiện tại
        return Evaluate(notificationType, incomingOrderVersion, currentAggregateState);
    }
    /// <summary>
    /// Đánh giá xem sự kiện hoặc thông báo có bị coi là lỗi thời dựa trên watermark của aggregate không (backwards-compatible).
    /// </summary>
    public static StaleEvaluationResult Evaluate(
        string notificationType,
        long incomingOrderVersion,
        NotificationAggregateWatermark? watermark)
    {
        if (watermark == null)
        {
            return Evaluate(notificationType, incomingOrderVersion, (AggregateStateProjection?)null);
        }

        var projection = new AggregateStateProjection(
            Scope: watermark.Scope,
            AggregateId: watermark.AggregateId,
            MaxVersion: watermark.LastOrderVersion,
            IsPaid: watermark.IsPaid,
            IsTerminal: watermark.IsTerminal,
            TerminalState: watermark.TerminalState,
            TerminalVersion: watermark.IsTerminal ? watermark.LastOrderVersion : null);

        return Evaluate(notificationType, incomingOrderVersion, projection);
    }
}
