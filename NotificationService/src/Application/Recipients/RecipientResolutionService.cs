using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Recipients;

public sealed record ResolvedRecipient(
    Guid RecipientUserId,
    string RecipientRole,
    string NotificationType,
    string Category,
    string Severity,
    bool IsActionable,
    string DeepLinkUrl,
    string? ActionUrl);

public interface IRecipientResolutionService
{
    Result<IReadOnlyList<ResolvedRecipient>> ResolveRecipients(
        string eventType,
        Guid orderId,
        Guid? renterId,
        Guid? supplierId,
        Guid? returnRequestId = null);
}

public sealed class RecipientResolutionService : IRecipientResolutionService
{
    public Result<IReadOnlyList<ResolvedRecipient>> ResolveRecipients(
        string eventType,
        Guid orderId,
        Guid? renterId,
        Guid? supplierId,
        Guid? returnRequestId = null)
    {
        if (orderId == Guid.Empty)
        {
            return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                Error.Validation("INVALID_RESOURCE_ID", "orderId không thể là GUID rỗng."));
        }

        var results = new List<ResolvedRecipient>();

        switch (eventType)
        {
            case "RentalOrderCreated":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalOrderCreated yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "ORDER_CREATED",
                    Category: "ORDER",
                    Severity: "ACTION_REQUIRED",
                    IsActionable: true,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: $"/checkout/payment?orderId={orderId}"));
                break;

            case "RentalOrderPaid":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalOrderPaid yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "ORDER_PAID",
                    Category: "PAYMENT",
                    Severity: "SUCCESS",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            case "RentalSupplierConfirmationRequested":
                if (!supplierId.HasValue || supplierId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalSupplierConfirmationRequested yêu cầu supplierId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: supplierId.Value,
                    RecipientRole: "Supplier",
                    NotificationType: "SUPPLIER_CONFIRMATION_REQUIRED",
                    Category: "ORDER",
                    Severity: "ACTION_REQUIRED",
                    IsActionable: true,
                    DeepLinkUrl: $"/supplier/sub-orders/{orderId}",
                    ActionUrl: $"/supplier/sub-orders/{orderId}/confirm"));
                break;

            case "RentalOrderExpired":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalOrderExpired yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "ORDER_EXPIRED",
                    Category: "ORDER",
                    Severity: "WARNING",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            case "RentalOrderCancelled":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalOrderCancelled yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "ORDER_CANCELLED",
                    Category: "ORDER",
                    Severity: "WARNING",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            case "RentalItemHandoverCompleted":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalItemHandoverCompleted yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "HANDOVER_COMPLETED",
                    Category: "HANDOVER",
                    Severity: "SUCCESS",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            case "RentalHandoverOverdue":
                if (!supplierId.HasValue || supplierId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalHandoverOverdue yêu cầu supplierId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: supplierId.Value,
                    RecipientRole: "Supplier",
                    NotificationType: "HANDOVER_OVERDUE_SUPPLIER",
                    Category: "HANDOVER",
                    Severity: "ACTION_REQUIRED",
                    IsActionable: true,
                    DeepLinkUrl: $"/supplier/sub-orders/{orderId}/handover",
                    ActionUrl: $"/supplier/sub-orders/{orderId}/handover"));

                if (renterId.HasValue && renterId.Value != Guid.Empty)
                {
                    results.Add(new ResolvedRecipient(
                        RecipientUserId: renterId.Value,
                        RecipientRole: "Renter",
                        NotificationType: "HANDOVER_OVERDUE_RENTER",
                        Category: "HANDOVER",
                        Severity: "INFO",
                        IsActionable: false,
                        DeepLinkUrl: $"/renter/orders/{orderId}",
                        ActionUrl: null));
                }
                break;

            case "ReturnConfirmationOverdue":
                if (!supplierId.HasValue || supplierId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "ReturnConfirmationOverdue yêu cầu supplierId hợp lệ."));
                }
                var returnLink = returnRequestId.HasValue && returnRequestId.Value != Guid.Empty
                    ? $"/supplier/returns/{returnRequestId.Value}"
                    : $"/supplier/returns/{orderId}";

                results.Add(new ResolvedRecipient(
                    RecipientUserId: supplierId.Value,
                    RecipientRole: "Supplier",
                    NotificationType: "RETURN_CONFIRMATION_OVERDUE_SUPPLIER",
                    Category: "RETURN",
                    Severity: "ACTION_REQUIRED",
                    IsActionable: true,
                    DeepLinkUrl: returnLink,
                    ActionUrl: returnLink));

                if (renterId.HasValue && renterId.Value != Guid.Empty)
                {
                    results.Add(new ResolvedRecipient(
                        RecipientUserId: renterId.Value,
                        RecipientRole: "Renter",
                        NotificationType: "RETURN_CONFIRMATION_OVERDUE_RENTER",
                        Category: "RETURN",
                        Severity: "INFO",
                        IsActionable: false,
                        DeepLinkUrl: $"/renter/orders/{orderId}",
                        ActionUrl: null));
                }
                break;

            case "RentalOrderCompleted":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "RentalOrderCompleted yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "ORDER_COMPLETED",
                    Category: "ORDER",
                    Severity: "SUCCESS",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            case "LatePaymentRefundRequested":
                if (!renterId.HasValue || renterId.Value == Guid.Empty)
                {
                    return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                        Error.Validation("MISSING_RECIPIENT", "LatePaymentRefundRequested yêu cầu renterId hợp lệ."));
                }
                results.Add(new ResolvedRecipient(
                    RecipientUserId: renterId.Value,
                    RecipientRole: "Renter",
                    NotificationType: "LATE_PAYMENT_REFUND_REQUESTED",
                    Category: "PAYMENT",
                    Severity: "INFO",
                    IsActionable: false,
                    DeepLinkUrl: $"/renter/orders/{orderId}",
                    ActionUrl: null));
                break;

            default:
                return Result.Failure<IReadOnlyList<ResolvedRecipient>>(
                    Error.Validation("UNSUPPORTED_EVENT_TYPE", $"Loại sự kiện '{eventType}' không được hỗ trợ xử lý thông báo."));
        }

        // Deduplicate recipients by (NotificationType, RecipientUserId)
        var deduped = results
            .GroupBy(r => (r.NotificationType, r.RecipientUserId))
            .Select(g => g.First())
            .ToList();

        return Result.Success<IReadOnlyList<ResolvedRecipient>>(deduped);
    }
}
