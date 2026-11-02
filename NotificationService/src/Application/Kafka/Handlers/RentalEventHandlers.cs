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

    public static string? GetStringOrDefault(this JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop) ? prop.GetString() : null;
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
}

public sealed class RentalOrderCreatedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCreated";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 1);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var totalPayableAmount = payload.GetDecimalOrDefault("totalPayableAmount", 0);
        var paymentDeadline = payload.GetStringOrDefault("paymentDeadline");

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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 2);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var paidAmount = payload.GetDecimalOrDefault("paidAmount", 0);
        var invoiceRef = payload.GetStringOrDefault("invoiceRef");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["paidAmount"] = paidAmount,
            ["invoiceRef"] = invoiceRef
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            AggregateId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "PAID",
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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var supplierId = payload.GetGuidOrDefault("supplierId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 2);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var deadline = payload.GetStringOrDefault("supplierConfirmationDeadline");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 99);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var reason = payload.GetStringOrDefault("reason") ?? "Hết thời gian thanh toán/xác nhận";

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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 99);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var reason = payload.GetStringOrDefault("reason") ?? "Hủy đơn";

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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 3);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var actualHandoverAt = payload.GetStringOrDefault("actualHandoverAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
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
            SupplierId: null,
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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var supplierId = payload.GetGuidOrDefault("supplierId");
        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 2);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var expectedStartDate = payload.GetStringOrDefault("expectedStartDate");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var supplierId = payload.GetGuidOrDefault("supplierId");
        var renterId = payload.GetGuidOrDefault("renterId");
        var returnRequestId = payload.GetGuidOrDefault("returnRequestId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 4);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var confirmationDeadlineAt = payload.GetStringOrDefault("confirmationDeadlineAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 10);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var completedAt = payload.GetStringOrDefault("completedAt");

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
        var orderId = payload.GetGuidOrDefault("orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "orderId không thể để trống."));
        }

        var renterId = payload.GetGuidOrDefault("renterId");
        var orderVersion = payload.GetInt64OrDefault("orderVersion", 100);
        var orderCode = payload.GetStringOrDefault("orderCode");
        var totalRefundAmount = payload.GetDecimalOrDefault("totalRefundAmount", 0);

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
