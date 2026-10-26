namespace Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;

// DTO cho các event cấp đơn trên rental.events.v1.
// Chỉ khai báo field Analytics dùng; field khác trong JSON (renterId thừa, invoiceRef, paymentRef...)
// bị bỏ qua khi đọc. OrderVersion là long? vì event lịch sử không có version (null, không phải 0).

/// <summary>Đơn được tạo: tạo dòng đơn và các dòng item kèm tiền, snapshot thiết bị.</summary>
public sealed record RentalOrderCreatedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required string OrderCode { get; init; }
    public required Guid RenterId { get; init; }
    public required string Currency { get; init; }
    public required decimal TotalRentalAmount { get; init; }
    public required decimal TotalDepositAmount { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required IReadOnlyList<RentalOrderCreatedItemPayload> Items { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Một dòng thuê trong RentalOrderCreated.</summary>
public sealed record RentalOrderCreatedItemPayload
{
    public required Guid RentalItemId { get; init; }
    public required Guid EquipmentListingId { get; init; }
    public required Guid SupplierId { get; init; }
    public required int Quantity { get; init; }
    public required DateOnly ExpectedStartDate { get; init; }
    public required DateOnly ExpectedEndDate { get; init; }
    public required decimal RentalAmount { get; init; }
    public required decimal DepositAmount { get; init; }

    // Snapshot thiết bị có thể thiếu ở dữ liệu gốc → không bắt buộc
    public Guid? EquipmentTypeId { get; init; }
    public string? EquipmentTypeName { get; init; }
    public Guid? BrandId { get; init; }
    public string? BrandName { get; init; }
    public string? Model { get; init; }
}

/// <summary>Đơn đã thanh toán: PaidAt áp dụng cho đơn và mọi item của đơn.</summary>
public sealed record RentalOrderPaidPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset PaidAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Đơn hoàn thành.</summary>
public sealed record RentalOrderCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public long? OrderVersion { get; init; }
}

/// <summary>Đơn bị hủy.</summary>
public sealed record RentalOrderCancelledPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }
    public required string Reason { get; init; } // Mã chuẩn, ví dụ RENTER_CANCELLED
    public long? OrderVersion { get; init; }
}

/// <summary>Đơn hết hạn.</summary>
public sealed record RentalOrderExpiredPayload
{
    public required Guid RentalOrderId { get; init; }
    public required DateTimeOffset ExpiredAt { get; init; }
    public required string Reason { get; init; } // Ví dụ PAYMENT_EXPIRED, HANDOVER_EXPIRED
    public long? OrderVersion { get; init; }
}
