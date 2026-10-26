namespace Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;

// DTO cho các event cấp item trên rental.events.v1. Mọi event đều mang rentalOrderId,
// nên dòng item luôn biết thuộc đơn nào kể cả khi đến trước RentalOrderCreated.

/// <summary>NCC chấp nhận dòng thuê → AcceptedCount.</summary>
public sealed record RentalItemConfirmedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required DateTimeOffset DecisionAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>NCC từ chối dòng thuê → RejectedCount.</summary>
public sealed record RentalItemRejectedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required DateTimeOffset DecisionAt { get; init; }
    public required string Reason { get; init; } // SUPPLIER_REJECTED
    public long? OrderVersion { get; init; }
}

/// <summary>Dòng thuê hết hạn → ConfirmationTimeoutCount khi reason là SUPPLIER_CONFIRMATION_EXPIRED.</summary>
public sealed record RentalItemExpiredPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required DateTimeOffset ExpiredAt { get; init; }
    public required string Reason { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Dòng thuê bị hủy.</summary>
public sealed record RentalItemCancelledPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public required string Reason { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Đã giao máy thật cho khách → RentedQuantity.</summary>
public sealed record RentalItemHandoverCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required int Quantity { get; init; }
    public required DateTimeOffset ActualHandoverAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Khách đã trả máy.</summary>
public sealed record RentalItemReturnedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required DateTimeOffset ReturnsCompletedAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>
/// Dòng thuê hoàn thành → CompletedRentalValue. Payload chưa có completedAt (chờ R2),
/// nên tạm dùng occurredAt của envelope theo contract mục 4.
/// </summary>
public sealed record RentalItemCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public long? OrderVersion { get; init; }
}
