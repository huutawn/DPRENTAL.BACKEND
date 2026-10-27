using Dprental.RentalService.Application.Hubs;

namespace Dprental.RentalService.Application.DTOs.Payment;

/// <summary>
/// Các kết quả nghiệp vụ khi áp dụng thanh toán cho đơn thuê.
/// </summary>
public enum PaymentOutcome
{
    Paid,
    RecoveryPending,
    RefundRequired,
    Unchanged
}

/// <summary>
/// Metadata invalidation thời gian thực đi kèm kết quả payment.
/// </summary>
public sealed record PaymentNotificationMetadata(
    RenterOrderChangedNotification? RenterNotification,
    IReadOnlyList<(Guid SupplierId, SupplierSubOrderChangedNotification Notification)> SupplierNotifications)
{
    public static readonly PaymentNotificationMetadata Empty = new(
        null,
        Array.Empty<(Guid, SupplierSubOrderChangedNotification)>());
}

/// <summary>
/// Kết quả chi tiết của ApplyInvoicePaidStagedAsync.
/// </summary>
public sealed record PaymentApplyResult(
    PaymentOutcome Outcome,
    bool Changed,
    PaymentNotificationMetadata Metadata)
{
    public static PaymentApplyResult Paid(PaymentNotificationMetadata metadata) =>
        new(PaymentOutcome.Paid, true, metadata);

    public static PaymentApplyResult RecoveryPending(PaymentNotificationMetadata metadata) =>
        new(PaymentOutcome.RecoveryPending, true, metadata);

    public static PaymentApplyResult RefundRequired(PaymentNotificationMetadata metadata) =>
        new(PaymentOutcome.RefundRequired, true, metadata);

    public static PaymentApplyResult Unchanged() =>
        new(PaymentOutcome.Unchanged, false, PaymentNotificationMetadata.Empty);
}
