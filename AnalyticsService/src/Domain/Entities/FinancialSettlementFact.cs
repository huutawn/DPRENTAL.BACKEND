namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Kết quả quyết toán một dòng thuê do Payment chốt. Analytics chỉ ghi nhận, không tự tính lại.
/// </summary>
public sealed class FinancialSettlementFact
{
    public string SettlementRef { get; set; } = string.Empty; // Khóa chính; mã quyết toán Payment gửi, dùng chống trùng

    public Guid RentalItemId { get; set; }  // Dòng thuê được quyết toán; nối với item để biết loại máy, NCC
    public Guid RentalOrderId { get; set; } // Đơn chứa dòng thuê

    // Payment chưa gửi các field này trong RentalSettled (chờ P1) → nullable
    public Guid? SupplierId { get; set; }         // NCC → SupplierPayable theo NCC
    public long? RentalAmount { get; set; }       // Tiền thuê Payment ghi nhận; chỉ để đối soát
    public long? DepositAmount { get; set; }      // Tiền cọc Payment ghi nhận; chỉ để đối soát
    public long? PlatformFeeAmount { get; set; }  // Phí nền tảng → PlatformRevenue
    public long? SupplierPayable { get; set; }    // Tiền phải trả NCC → SupplierPayable

    public long FinalDeductionAmount { get; set; } // Số tiền trừ vào cọc (bồi thường hư hỏng) → DeductionAmount

    // Số cọc phải hoàn, chưa phải tiền đã hoàn; không cộng vào RefundAmount
    public long DepositRefundDue { get; set; }

    public DateTimeOffset CompletedAt { get; set; } // Lúc quyết toán xong; ngày của các KPI tài chính trên
}
