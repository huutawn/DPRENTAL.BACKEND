using System.Text.Json;
using Dprental.NotificationService.Application.Kafka.Contracts;
using Dprental.NotificationService.Common;
using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Application.Kafka.Handlers;

internal static class JsonElementExtensions
{
    public static Guid? GetGuidOrDefault(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String &&
                Guid.TryParse(prop.GetString(), out var guid) &&
                guid != Guid.Empty)
            {
                return guid;
            }
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
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
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

    public static string? GetUtcTimestampOrDefault(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                if (!string.IsNullOrWhiteSpace(str) &&
                    DateTimeOffset.TryParse(str, out var dto) &&
                    dto.Offset == TimeSpan.Zero)
                {
                    return str;
                }
            }
        }
        return null;
    }

    public static string? GetUtcTimestampFromAny(this JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var val = root.GetUtcTimestampOrDefault(name);
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

    public static Result ValidateGuidPropertyIfPresent(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return Result.Success();
            }

            if (prop.ValueKind != JsonValueKind.String)
            {
                return Result.Failure(Error.Validation("INVALID_GUID_KIND", $"Trường '{propertyName}' phải là kiểu chuỗi (String)."));
            }

            var str = prop.GetString();
            if (string.IsNullOrWhiteSpace(str) || !Guid.TryParse(str, out var guid) || guid == Guid.Empty)
            {
                return Result.Failure(Error.Validation("INVALID_GUID_VALUE", $"Trường '{propertyName}' phải là GUID hợp lệ và khác Guid.Empty."));
            }
        }
        return Result.Success();
    }

    public static Result ValidateUtcTimestampPropertyIfPresent(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return Result.Success();
            }

            if (prop.ValueKind != JsonValueKind.String)
            {
                return Result.Failure(Error.Validation("INVALID_TIMESTAMP_KIND", $"Trường '{propertyName}' phải là kiểu chuỗi (String)."));
            }

            var str = prop.GetString();
            if (string.IsNullOrWhiteSpace(str) || !DateTimeOffset.TryParse(str, out var dto) || dto.Offset != TimeSpan.Zero)
            {
                return Result.Failure(Error.Validation("INVALID_UTC_TIMESTAMP", $"Trường '{propertyName}' phải là thời gian UTC hợp lệ."));
            }
        }
        return Result.Success();
    }

    public static Result ValidateNumberPropertyIfPresent(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return Result.Success();
            }

            if (prop.ValueKind != JsonValueKind.Number)
            {
                return Result.Failure(Error.Validation("INVALID_NUMBER_KIND", $"Trường '{propertyName}' phải là kiểu số (Number)."));
            }
        }
        return Result.Success();
    }

    public static Result ValidateStringPropertyIfPresent(this JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Null)
            {
                return Result.Success();
            }

            if (prop.ValueKind != JsonValueKind.String)
            {
                return Result.Failure(Error.Validation("INVALID_STRING_KIND", $"Trường '{propertyName}' phải là kiểu chuỗi (String)."));
            }
        }
        return Result.Success();
    }
    public static Result ValidateFields(this JsonElement root,
        string[]? guidFields = null,
        string[]? numberFields = null,
        string[]? utcTimestampFields = null,
        string[]? stringFields = null)
    {
        if (guidFields != null)
        {
            foreach (var field in guidFields)
            {
                var res = root.ValidateGuidPropertyIfPresent(field);
                if (res.IsFailure) return res;
            }
        }

        if (numberFields != null)
        {
            foreach (var field in numberFields)
            {
                var res = root.ValidateNumberPropertyIfPresent(field);
                if (res.IsFailure) return res;
            }
        }

        if (utcTimestampFields != null)
        {
            foreach (var field in utcTimestampFields)
            {
                var res = root.ValidateUtcTimestampPropertyIfPresent(field);
                if (res.IsFailure) return res;
            }
        }

        if (stringFields != null)
        {
            foreach (var field in stringFields)
            {
                var res = root.ValidateStringPropertyIfPresent(field);
                if (res.IsFailure) return res;
            }
        }

        return Result.Success();
    }
}

public sealed class RentalOrderCreatedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCreated";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version", "totalPayableAmount", "amount"],
            utcTimestampFields: ["paymentDeadline"],
            stringFields: ["orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version", "paidAmount", "amount"],
            utcTimestampFields: ["paidAt"],
            stringFields: ["orderCode", "invoiceRef", "paymentRef"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData,
            IsPaid: true));
    }
}

public sealed class RentalSupplierConfirmationRequestedHandler : IKafkaEventHandler
{
    public string EventType => "RentalSupplierConfirmationRequested";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "supplierId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["supplierConfirmationDeadline"],
            stringFields: ["orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["expiredAt"],
            stringFields: ["orderCode", "reason"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["cancelledAt"],
            stringFields: ["orderCode", "reason"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId", "supplierId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["actualHandoverAt", "completedAt"],
            stringFields: ["orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "supplierId", "renterId", "userId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["overdueAt"],
            stringFields: ["orderCode", "expectedStartDate"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: itemId.HasValue ? "RentalItem" : "RentalOrder",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: itemId.HasValue ? NotificationWatermarkScope.RentalItem : NotificationWatermarkScope.RentalOrder,
            WatermarkId: itemId ?? orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "supplierId", "renterId", "userId", "returnRequestId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["confirmationDeadlineAt"],
            stringFields: ["orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var supplierId = payload.GetGuidFromAny("supplierId");
        var renterId = payload.GetGuidFromAny("renterId", "userId");
        var returnRequestId = payload.GetGuidFromAny("returnRequestId");
        if (!returnRequestId.HasValue || returnRequestId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_RETURN_REQUEST_ID", "returnRequestId không thể để trống."));
        }

        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        if (!itemId.HasValue || itemId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_RENTAL_ITEM_ID", "rentalItemId không thể để trống."));
        }

        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var confirmationDeadlineAt = payload.GetStringFromAny("confirmationDeadlineAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId.Value,
            ["orderCode"] = orderCode,
            ["returnRequestId"] = returnRequestId.Value,
            ["confirmationDeadlineAt"] = confirmationDeadlineAt
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "ReturnRequest",
            ResourceId: returnRequestId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.ReturnRequest,
            WatermarkId: returnRequestId.Value,
            OrderVersion: orderVersion,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: returnRequestId.Value,
            RenderData: renderData,
            RentalItemId: itemId.Value));
    }
}
public sealed class RentalOrderCompletedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["completedAt"],
            stringFields: ["orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId"],
            numberFields: ["orderVersion", "version", "totalRefundAmount", "amount"],
            utcTimestampFields: ["requestedAt"],
            stringFields: ["orderCode", "refundReason"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId", "customerId", "supplierId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["decisionAt"],
            stringFields: ["orderCode", "inventoryConfirmationRef"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "renterId", "userId", "customerId", "supplierId", "rentalItemId", "itemId"],
            numberFields: ["orderVersion", "version"],
            utcTimestampFields: ["decisionAt"],
            stringFields: ["orderCode", "reason", "optionalNote", "note"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

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
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "REJECTED",
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
        var validation = payload.ValidateFields(
            guidFields: ["rentalOrderId", "orderId", "refundId", "rentalItemId", "itemId", "renterId", "userId"],
            numberFields: ["orderVersion", "version", "amount", "refundAmount"],
            utcTimestampFields: ["completedAt"],
            stringFields: ["refundRef", "refundKind", "currency", "orderCode"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g2) && g2 != Guid.Empty ? g2 : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var refundId = payload.GetGuidFromAny("refundId");
        if (!refundId.HasValue || refundId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_REFUND_ID", "refundId không thể để trống."));
        }
        var refundRef = payload.GetStringFromAny("refundRef") ?? $"REF-{refundId.Value.ToString()[..8]}";
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var renterId = payload.GetGuidFromAny("renterId", "userId");
        if (!renterId.HasValue || renterId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_RENTER_ID", "renterId không thể để trống."));
        }
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
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "Refund",
            ResourceId: refundId?.ToString() ?? Guid.NewGuid().ToString(),
            WatermarkScope: null,
            WatermarkId: null,
            OrderVersion: 0,
            IsTerminal: false,
            TerminalState: null,
            RenterId: renterId,
            SupplierId: null,
            ReturnRequestId: null,
            RenderData: renderData,
            FinancialId: refundId));
    }
}

public sealed class SupplierPayoutCompletedHandler : IKafkaEventHandler
{
    public string EventType => "SupplierPayoutCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var validation = payload.ValidateFields(
            guidFields: ["settlementId", "rentalOrderId", "orderId", "payoutId", "rentalItemId", "itemId", "supplierId"],
            numberFields: ["orderVersion", "version", "amount", "payoutAmount"],
            utcTimestampFields: ["completedAt"],
            stringFields: ["payoutRef", "currency"]);
        if (validation.IsFailure)
        {
            return Result.Failure<NotificationEventIntent>(validation.Error);
        }

        var settlementId = payload.GetGuidFromAny("settlementId");
        if (!settlementId.HasValue || settlementId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_SETTLEMENT_ID", "settlementId không thể để trống."));
        }

        var payoutId = payload.GetGuidFromAny("payoutId");
        if (!payoutId.HasValue || payoutId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_PAYOUT_ID", "payoutId không thể để trống."));
        }
        var payoutRef = payload.GetStringFromAny("payoutRef") ?? $"PAYOUT-{payoutId.Value.ToString()[..8]}";
        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        if (!supplierId.HasValue || supplierId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_SUPPLIER_ID", "supplierId không thể để trống."));
        }
        var amount = payload.GetDecimalFromAny(0, "amount", "payoutAmount");
        var currency = payload.GetStringFromAny("currency") ?? "VND";
        var completedAt = payload.GetStringFromAny("completedAt");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");

        var realOrderId = payload.GetGuidFromAny("rentalOrderId", "orderId");
        if (realOrderId.HasValue && realOrderId.Value == Guid.Empty)
        {
            realOrderId = null;
        }

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
            ["deepLinkUrl"] = "/own/orders"
        };

        if (realOrderId.HasValue)
        {
            renderData["rentalOrderId"] = realOrderId.Value;
        }

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: realOrderId,
            ResourceType: "Settlement",
            ResourceId: settlementId.Value.ToString(),
            WatermarkScope: null,
            WatermarkId: null,
            OrderVersion: 0,
            IsTerminal: false,
            TerminalState: null,
            RenterId: null,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData,
            FinancialId: settlementId.Value));
    }
}

public sealed class RentalOrderRejectedHandler : IKafkaEventHandler
{
    public string EventType => "RentalOrderRejected";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g) && g != Guid.Empty ? g : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var reason = payload.GetStringFromAny("reason") ?? "Đơn thuê đã bị từ chối";
        var rejectedAt = payload.GetStringFromAny("rejectedAt", "decisionAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["orderCode"] = orderCode,
            ["renterId"] = renterId,
            ["supplierId"] = supplierId,
            ["reason"] = reason,
            ["rejectedAt"] = rejectedAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalOrder",
            ResourceId: orderId.Value.ToString(),
            WatermarkScope: NotificationWatermarkScope.RentalOrder,
            WatermarkId: orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "REJECTED",
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemExpiredHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemExpired";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g) && g != Guid.Empty ? g : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var reason = payload.GetStringFromAny("reason") ?? "Hết thời gian thanh toán/xác nhận thiết bị";
        var expiredAt = payload.GetStringFromAny("expiredAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["renterId"] = renterId,
            ["supplierId"] = supplierId,
            ["reason"] = reason,
            ["expiredAt"] = expiredAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        // Item scope: Không phải terminal state của toàn bộ aggregate order
        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "EXPIRED",
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemCancelledHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemCancelled";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g) && g != Guid.Empty ? g : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var reason = payload.GetStringFromAny("reason") ?? "Hủy thiết bị trong đơn thuê";
        var cancelledAt = payload.GetStringFromAny("cancelledAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["renterId"] = renterId,
            ["supplierId"] = supplierId,
            ["reason"] = reason,
            ["cancelledAt"] = cancelledAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        // Item scope: Không phải terminal state của toàn bộ aggregate order
        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "CANCELLED",
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}

public sealed class RentalItemReturnedHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemReturned";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g) && g != Guid.Empty ? g : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var returnRequestId = payload.GetGuidFromAny("returnRequestId");
        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var returnedAt = payload.GetStringFromAny("returnsCompletedAt", "returnedAt", "actualReturnAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["returnRequestId"] = returnRequestId,
            ["orderCode"] = orderCode,
            ["renterId"] = renterId,
            ["supplierId"] = supplierId,
            ["returnedAt"] = returnedAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "RETURNED",
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: returnRequestId,
            RenderData: renderData,
            RentalItemId: itemId));
    }
}

public sealed class RentalItemCompletedHandler : IKafkaEventHandler
{
    public string EventType => "RentalItemCompleted";

    public Result<NotificationEventIntent> ParseIntent(KafkaMessageEnvelope envelope)
    {
        var payload = envelope.Payload;
        var orderId = payload.GetGuidFromAny("rentalOrderId", "orderId") ?? (Guid.TryParse(envelope.AggregateId, out var g) && g != Guid.Empty ? g : null);
        if (!orderId.HasValue || orderId.Value == Guid.Empty)
        {
            return Result.Failure<NotificationEventIntent>(Error.Validation("MISSING_ORDER_ID", "rentalOrderId không thể để trống."));
        }

        var itemId = payload.GetGuidFromAny("rentalItemId", "itemId");
        var renterId = payload.GetGuidFromAny("renterId", "userId", "customerId");
        var supplierId = payload.GetGuidFromAny("supplierId");
        var orderVersion = payload.GetInt64FromAny(1, "orderVersion", "version");
        var orderCode = payload.GetStringFromAny("orderCode") ?? string.Empty;
        var completedAt = payload.GetStringFromAny("completedAt");

        var renderData = new Dictionary<string, object?>
        {
            ["rentalOrderId"] = orderId.Value,
            ["rentalItemId"] = itemId,
            ["orderCode"] = orderCode,
            ["renterId"] = renterId,
            ["supplierId"] = supplierId,
            ["completedAt"] = completedAt,
            ["orderVersion"] = orderVersion,
            ["deepLinkUrl"] = $"/orders/{orderId.Value}"
        };

        return Result.Success(new NotificationEventIntent(
            EventType: EventType,
            OrderId: orderId.Value,
            ResourceType: "RentalItem",
            ResourceId: (itemId.HasValue ? itemId.Value.ToString() : orderId.Value.ToString()),
            WatermarkScope: NotificationWatermarkScope.RentalItem,
            WatermarkId: itemId ?? orderId.Value,
            OrderVersion: orderVersion,
            IsTerminal: true,
            TerminalState: "COMPLETED",
            RenterId: renterId,
            SupplierId: supplierId,
            ReturnRequestId: null,
            RenderData: renderData));
    }
}
