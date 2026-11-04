using System.Text.Json;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

internal static class JsonElementExtensions
{
    public static Guid? GetGuidOrDefault(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) &&
            Guid.TryParse(prop.GetString(), out var guid))
        {
            return guid;
        }
        return null;
    }

    public static Guid? GetGuidFromAny(this JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var val = root.GetGuidOrDefault(name);
            if (val.HasValue && val.Value != Guid.Empty)
            {
                return val;
            }
        }
        return null;
    }

    public static string? GetStringOrDefault(this JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
    }

    public static string? GetStringFromAny(this JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var val = root.GetStringOrDefault(name);
            if (!string.IsNullOrWhiteSpace(val))
            {
                return val;
            }
        }
        return null;
    }

    public static long GetInt64OrDefault(this JsonElement root, string propertyName, long defaultValue = 0)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
            {
                return val;
            }
        }
        return defaultValue;
    }

    public static long GetInt64FromAny(this JsonElement root, long defaultValue, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val))
                {
                    return val;
                }
            }
        }
        return defaultValue;
    }

    public static decimal GetDecimalOrDefault(this JsonElement root, string propertyName, decimal defaultValue = 0)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var val))
            {
                return val;
            }
        }
        return defaultValue;
    }

    public static decimal GetDecimalFromAny(this JsonElement root, decimal defaultValue, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var val))
                {
                    return val;
                }
            }
        }
        return defaultValue;
    }
}

public sealed class RentalOrderCreatedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCreated";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var totalPayableAmount = payload.GetDecimalFromAny(0, "totalPayableAmount", "amount");
        var paymentDeadline = payload.GetStringFromAny("paymentDeadline");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["totalPayableAmount"] = totalPayableAmount,
            ["paymentDeadline"] = paymentDeadline
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalOrderPaidHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderPaid";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var paidAmount = payload.GetDecimalFromAny(0, "paidAmount", "amount");
        var invoiceRef = payload.GetStringFromAny("invoiceRef");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["paidAmount"] = paidAmount,
            ["invoiceRef"] = invoiceRef
        };

        // PAID là mốc xác nhận thanh toán thành công, KHÔNG PHẢI trạng thái kết thúc toàn bộ vòng đời đơn hàng
        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalSupplierConfirmationRequestedHandler : IKafkaEventHandler
{
    public string EventType => "RentalSupplierConfirmationRequested";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var supplierId = payload.GetGuidFromAny("supplierId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var deadline = payload.GetStringFromAny("supplierConfirmationDeadline");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["supplierConfirmationDeadline"] = deadline
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: null,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalOrderExpiredHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderExpired";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var reason = payload.GetStringFromAny("reason") ?? "Hết thời gian thanh toán/xác nhận";

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["reason"] = reason
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "EXPIRED",
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalOrderCancelledHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCancelled";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var reason = payload.GetStringFromAny("reason") ?? "Hủy đơn";

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["reason"] = reason
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "CANCELLED",
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemHandoverCompletedHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemHandoverCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var actualHandoverAt = payload.GetStringFromAny("actualHandoverAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["actualHandoverAt"] = actualHandoverAt
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalHandoverOverdueHandler : IKafkaEventHandler
{
    public string EventType => "RentalHandoverOverdue";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var supplierId = payload.GetGuidFromAny("supplierId");
        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var expectedStartDate = payload.GetStringFromAny("expectedStartDate");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["expectedStartDate"] = expectedStartDate
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class ReturnConfirmationOverdueHandler : IKafkaEventHandler
{
    public string EventType => "ReturnConfirmationOverdue";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var supplierId = payload.GetGuidFromAny("supplierId");
        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var returnRequestId = payload.GetGuidFromAny("returnRequestId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var confirmationDeadlineAt = payload.GetStringFromAny("confirmationDeadlineAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["returnRequestId"] = returnRequestId,
            ["confirmationDeadlineAt"] = confirmationDeadlineAt
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: returnRequestId,
            RenderData: renderData));
    }
}

public sealed class RentalOrderCompletedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var completedAt = payload.GetStringFromAny("completedAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["completedAt"] = completedAt
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "COMPLETED",
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class LatePaymentRefundRequestedHandler : IKafkaEventHandler
{
    public string EventType => "LatePaymentRefundRequested";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var totalRefundAmount = payload.GetDecimalFromAny(0, "totalRefundAmount", "amount");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["totalRefundAmount"] = totalRefundAmount
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemConfirmedHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemConfirmed";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g0) && g0 != Guid.Empty ? g0 : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var decisionAt = payload.GetStringFromAny("decisionAt");
        var inventoryConfirmationRef = payload.GetStringFromAny("inventoryConfirmationRef") ?? string.Empty;

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["supplierId"] = supplierId,
            ["renterId"] = renterId,
            ["orderCode"] = orderCode,
            ["decisionAt"] = decisionAt,
            ["inventoryConfirmationRef"] = inventoryConfirmationRef,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/renter/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemRejectedHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemRejected";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g1) && g1 != Guid.Empty ? g1 : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var decisionAt = payload.GetStringFromAny("decisionAt");
        var reason = payload.GetStringFromAny("reason") ?? "Nhà cung cấp từ chối cung cấp thiết bị";
        var optionalNote = payload.GetStringFromAny("optionalNote", "note");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["supplierId"] = supplierId,
            ["renterId"] = renterId,
            ["orderCode"] = orderCode,
            ["decisionAt"] = decisionAt,
            ["reason"] = reason,
            ["optionalNote"] = optionalNote,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/renter/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RefundCompletedFinancialHandler : IKafkaEventHandler
{
    public string EventType => "RefundCompletedFinancial";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g2) && g2 != Guid.Empty ? g2 : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var refundId = payload.GetGuidFromAny("refundId");
        var refundRef = payload.GetStringFromAny("refundRef") ?? (refundId.HasValue ? $"REF-{refundId.Value.ToString()[..8]}" : "REF-UNKNOWN");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var refundKind = payload.GetStringFromAny("refundKind") ?? "FULL_CANCELLATION";
        var amount = payload.GetDecimalFromAny(0, "amount", "refundAmount");
        var currency = payload.GetStringFromAny("currency") ?? "VND";
        var completedAt = payload.GetStringFromAny("completedAt");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["refundId"] = refundId,
            ["refundRef"] = refundRef,
            ["rentalItemId"] = itemId,
            ["renterId"] = renterId,
            ["refundKind"] = refundKind,
            ["amount"] = amount,
            ["currency"] = currency,
            ["completedAt"] = completedAt,
            ["orderCode"] = orderCode,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/renter/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class SupplierPayoutCompletedHandler : IKafkaEventHandler
{
    public string EventType => "SupplierPayoutCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var settlementId = payload.GetGuidFromAny("settlementId", "rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g3) && g3 != Guid.Empty ? g3 : null);
        if (!settlementId.HasValue || settlementId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_SETTLEMENT_ID", "settlementId không thể để trống."));
        }

        var payoutId = payload.GetGuidFromAny("payoutId");
        var payoutRef = payload.GetStringFromAny("payoutRef") ?? (payoutId.HasValue ? $"PAYOUT-{payoutId.Value.ToString()[..8]}" : "PAYOUT-UNKNOWN");
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var amount = payload.GetDecimalFromAny(0, "amount", "payoutAmount");
        var currency = payload.GetStringFromAny("currency") ?? "VND";
        var completedAt = payload.GetStringFromAny("completedAt");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");

        var renderData = new Dictionary<string, object?>
        {
            ["settlementId"] = settlementId.Value,
            ["payoutId"] = payoutId,
            ["payoutRef"] = payoutRef,
            ["rentalItemId"] = itemId,
            ["supplierId"] = supplierId,
            ["amount"] = amount,
            ["currency"] = currency,
            ["completedAt"] = completedAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/supplier/settlements/{settlementId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: settlementId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: null,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}
