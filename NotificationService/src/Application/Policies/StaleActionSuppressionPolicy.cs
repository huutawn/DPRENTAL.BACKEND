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

        // Trường hợp 1: Order/Aggregate đã ở trạng thái terminal (CANCELLED, COMPLETED, EXPIRED, REJECTED)
        // PAID milestone KHÔNG coi là terminal
        var isPaidMilestone = aggregateState.IsPaid || string.Equals(aggregateState.TerminalState, "PAID", StringComparison.OrdinalIgnoreCase);
        var isTerminal = !isPaidMilestone && (
            (!string.IsNullOrWhiteSpace(aggregateState.TerminalState) && TerminalStates.Contains(aggregateState.TerminalState)) ||
            (aggregateState.IsTerminal && (string.IsNullOrWhiteSpace(aggregateState.TerminalState) || TerminalStates.Contains(aggregateState.TerminalState)))
        );

        if (isTerminal)
        {
            var terminal = !string.IsNullOrWhiteSpace(aggregateState.TerminalState) ? aggregateState.TerminalState.ToUpperInvariant() : "TERMINAL";

            // Khi ở trạng thái terminal, CHỈ chặn các thông báo có tính hành động (actionable)
            if (ActionableNotificationTypes.Contains(notificationType))
            {
                return new StaleEvaluationResult(
                    IsStale: true,
                    SuppressAction: true,
                    SuppressEmail: true,
                    Reason: $"Đơn thuê đã ở trạng thái kết thúc ({terminal}).");
            }

            return new StaleEvaluationResult(
                IsStale: false,
                SuppressAction: false,
                SuppressEmail: false,
                Reason: null);
        }

        // Trường hợp 2: Milestone PAID (đã thanh toán)
        // Ghi nhận mốc thanh toán; chặn CTA thanh toán của ORDER_CREATED nhưng KHÔNG chặn xác nhận của nhà cung cấp và KHÔNG coi là terminal
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

        // Trường hợp 3: incomingOrderVersion nhỏ hơn phiên bản watermark hiện tại
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
