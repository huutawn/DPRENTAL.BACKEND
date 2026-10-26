namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Một lần nền tảng chuyển tiền cho nhà cung cấp. Không lưu thông tin tài khoản nhận tiền.
/// </summary>
public sealed class SupplierPayoutFact
{
    public string PayoutRef { get; set; } = string.Empty; // Khóa chính; mã lần chuyển tiền, dùng chống trùng

    public Guid RentalItemId { get; set; } // Dòng thuê được chi trả
    public Guid SupplierId { get; set; }   // NCC nhận tiền → lọc theo NCC

    public long Amount { get; set; } // Số tiền chuyển (VND) → SupplierPayoutCompleted

    public DateTimeOffset CreatedAt { get; set; } // Lúc tạo lệnh chuyển tiền

    // Null khi lệnh chuyển tiền chưa hoàn tất; ngày của SupplierPayoutCompleted
    public DateTimeOffset? CompletedAt { get; set; }

    // PENDING_PAYOUT hoặc COMPLETED, giữ nguyên giá trị Payment gửi; chỉ cộng dòng COMPLETED
    public string Status { get; set; } = string.Empty;
}
