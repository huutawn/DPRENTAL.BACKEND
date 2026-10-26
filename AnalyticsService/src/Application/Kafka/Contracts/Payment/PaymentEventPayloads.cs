namespace Dprental.AnalyticsService.Application.Kafka.Contracts.Payment;

// DTO cho các event trên payment.events.v1. Payment không gửi orderVersion; chống trùng
// bằng khóa tự nhiên (SettlementRef, RefundRef + RentalItemId) vì messageId của Payment là ngẫu nhiên.
// Không khai báo invoiceRef, paymentRef, operationKey: Analytics không cần.

/// <summary>
/// Kết quả quyết toán một dòng thuê. Field phí nền tảng / phải trả NCC chưa có (chờ P1);
/// khi Payment bổ sung thì thêm property ở đây.
/// </summary>
public sealed record RentalSettledPayload
{
    public required Guid RentalOrderId { get; init; }
    public required Guid RentalItemId { get; init; }
    public required string SettlementRef { get; init; }
    public required decimal FinalDeductionAmount { get; init; }
    public required decimal RefundedDepositAmount { get; init; } // = DepositRefundDue: cọc PHẢI hoàn, chưa phải đã hoàn
    public required string Currency { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>Một lần hoàn tiền, có thể gồm nhiều dòng thuê.</summary>
public sealed record RefundCompletedPayload
{
    public required Guid RentalOrderId { get; init; }
    public required string RefundRef { get; init; }
    public required string Currency { get; init; }
    public required IReadOnlyList<RefundCompletedItemPayload> Items { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>Phần hoàn tiền của một dòng thuê, tách tiền thuê và tiền cọc.</summary>
public sealed record RefundCompletedItemPayload
{
    public required Guid RentalItemId { get; init; }
    public required decimal RefundedRentalAmount { get; init; }
    public required decimal RefundedDepositAmount { get; init; }
}
