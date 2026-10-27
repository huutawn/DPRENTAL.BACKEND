namespace Dprental.AnalyticsService.Application.Events.Rental;

// DTO cho rental.events.v1. Chỉ khai báo field Analytics lưu; field khác (renterId, invoiceRef, địa chỉ...)
// bị bỏ qua khi đọc nên dữ liệu nhạy cảm không bao giờ vào bộ nhớ Analytics.
// OrderVersion là long? vì event lịch sử không có version: null nghĩa là "không biết", không phải 0 hay 1.

public sealed record RentalOrderCreatedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<RentalOrderCreatedItemPayload> Items { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalOrderCreatedItemPayload
{
    public required Guid RentalItemId { get; init; }
    public required Guid EquipmentListingId { get; init; }
    public required Guid SupplierId { get; init; }
    public required int Quantity { get; init; }
    public required decimal RentalAmount { get; init; }
    public required decimal DepositAmount { get; init; }

    // Snapshot thiết bị có thể thiếu ở dữ liệu gốc → không bắt buộc, không bịa giá trị
    public Guid? EquipmentTypeId { get; init; }
    public string? EquipmentTypeName { get; init; }
    public Guid? BrandId { get; init; }
    public string? BrandName { get; init; }
    public string? Model { get; init; }
}

public sealed record RentalOrderPaidPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset PaidAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalOrderCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalOrderCancelledPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalOrderExpiredPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset ExpiredAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalOrderRejectedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset RejectedAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemConfirmedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public Guid? SupplierId { get; init; }
    public required DateTimeOffset DecisionAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemRejectedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public Guid? SupplierId { get; init; }
    public required DateTimeOffset DecisionAt { get; init; }
    public string? Reason { get; init; } // SUPPLIER_REJECTED → RejectedCount
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemExpiredPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public Guid? SupplierId { get; init; }
    public required DateTimeOffset ExpiredAt { get; init; }
    public string? Reason { get; init; } // SUPPLIER_CONFIRMATION_EXPIRED → ConfirmationTimeoutCount
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemCancelledPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemHandoverCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public Guid? SupplierId { get; init; }
    public int? Quantity { get; init; }
    public required DateTimeOffset ActualHandoverAt { get; init; } // → RentedQuantity
    public long? OrderVersion { get; init; }
}

public sealed record RentalItemReturnedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required DateTimeOffset ReturnsCompletedAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Chưa có completedAt (chờ R2): dùng occurredAt của phong bì, theo contract mục 4.</summary>
public sealed record RentalItemCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public long? OrderVersion { get; init; }
}
