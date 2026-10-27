namespace Dprental.AnalyticsService.Application.Events.Payment;

// DTO cho payment.analytics.v1. Dùng required thay vì record vị trí: thiếu field thì đọc thất bại
// (dead letter), không lặng lẽ thành 0 hay Guid.Empty rồi bị cộng vào báo cáo.
// Không có thông tin tài khoản ngân hàng / người nhận.

public sealed record InvoicePaidFinancialPayload
{
    public required Guid InvoiceId { get; init; }
    public required string InvoiceRef { get; init; }
    public required Guid RentalOrderId { get; init; }
    public required Guid RenterId { get; init; }
    public required decimal RentalAmount { get; init; }
    public required decimal DepositAmount { get; init; }
    public required decimal TotalAmount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset PaidAt { get; init; }
}

/// <summary>Kết quả quyết toán do Payment chốt. Analytics không tự tính lại phí hay tiền phải trả NCC.</summary>
public sealed record RentalItemFinancialSettledPayload
{
    public required Guid SettlementId { get; init; }
    public required string SettlementRef { get; init; }
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required decimal RentalAmount { get; init; }
    public required decimal DepositAmount { get; init; }
    public required decimal FinalDeductionAmount { get; init; }
    public required decimal DepositRefundDue { get; init; }
    public required decimal PlatformFeeRate { get; init; }
    public required decimal PlatformFeeAmount { get; init; }
    public required decimal SupplierPayable { get; init; }
    public string? Currency { get; init; } // Payment hiện không gửi; nếu có thì phải là VND
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>Một lần hoàn tiền. RentalItemId null = hoàn cấp đơn.</summary>
public sealed record RefundCompletedFinancialPayload
{
    public required Guid RefundId { get; init; }
    public required string RefundRef { get; init; }
    public required Guid RentalOrderId { get; init; }
    public Guid? RentalItemId { get; init; }
    public required Guid RenterId { get; init; }
    public required string RefundKind { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

public sealed record SupplierPayoutCompletedPayload
{
    public required Guid PayoutId { get; init; }
    public required string PayoutRef { get; init; }
    public required Guid SettlementId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required Guid SupplierId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
