using Dprental.AnalyticsService.Common.Enums;

namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Một dòng read model cho một đơn thuê. Chỉ chứa dữ liệu, không chứa quy tắc nghiệp vụ.
/// </summary>
public sealed class RentalOrderFact
{
    public Guid OrderId { get; set; } // Khóa chính; mọi event của đơn đều có rentalOrderId

    // Các cột chỉ có trong RentalOrderCreated → nullable (dòng tạm khi event đến sai thứ tự)
    public string? OrderCode { get; set; }   // Mã đơn dễ đọc, ví dụ RNT-20261023-0001
    public Guid? RenterId { get; set; }      // Người thuê
    public long? RentalAmount { get; set; }  // Tổng tiền thuê của đơn (VND); tham khảo, đối soát
    public long? DepositAmount { get; set; } // Tổng tiền cọc (VND); không phải doanh thu
    public int? ItemCount { get; set; }      // Số dòng thuê trong đơn
    public int? SupplierCount { get; set; }  // Số nhà cung cấp khác nhau trong đơn

    // Mỗi mốc do một event cấp đơn điền; null khi mốc đó chưa xảy ra
    public DateTimeOffset? CreatedAt { get; set; }   // RentalOrderCreated → TotalOrders
    public DateTimeOffset? PaidAt { get; set; }      // RentalOrderPaid → PaidOrders
    public DateTimeOffset? CompletedAt { get; set; } // RentalOrderCompleted → CompletedOrders
    public DateTimeOffset? CancelledAt { get; set; } // RentalOrderCancelled → CancelledOrders
    public DateTimeOffset? ExpiredAt { get; set; }   // RentalOrderExpired → ExpiredOrders
    public DateTimeOffset? RejectedAt { get; set; }  // RentalOrderRejected (chờ R1) → RejectedOrders

    public OrderFactStatus Status { get; set; } // Trạng thái hiện tại; dòng tạm mặc định PendingPayment

    // orderVersion của event cuối đã đổi Status; dùng để chặn event cũ kéo trạng thái lùi.
    // Null khi chỉ mới nhận event lịch sử không có version.
    public long? LastVersion { get; set; }

    // Ánh xạ tới cột hệ thống xmin của PostgreSQL để chống ghi đè đồng thời
    public uint RowVersion { get; set; }
}
