namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Phần tiền đã hoàn cho một dòng thuê trong một lần hoàn tiền. Khóa chính gồm RefundRef và RentalItemId.
/// </summary>
public sealed class RefundFact
{
    public string RefundRef { get; set; } = string.Empty; // Khóa chính (phần 1); mã lần hoàn tiền
    public Guid RentalItemId { get; set; }                // Khóa chính (phần 2); một lần hoàn có thể gồm nhiều item

    public Guid OrderId { get; set; } // Đơn chứa dòng thuê

    // Tách riêng tiền thuê để trừ đúng phần "đã hoàn" trong GMV (GMV không gồm cọc)
    public long RefundedRentalAmount { get; set; }  // Phần tiền thuê đã hoàn → GrossRentalValueRefunded
    public long RefundedDepositAmount { get; set; } // Phần tiền cọc đã hoàn; cộng với dòng trên → RefundAmount

    // Loại hoàn tiền (hủy, NCC từ chối...); Payment chưa gửi (chờ P4) → nullable
    public string? Kind { get; set; }

    public DateTimeOffset CompletedAt { get; set; } // Lúc hoàn xong → ngày của RefundAmount
}
