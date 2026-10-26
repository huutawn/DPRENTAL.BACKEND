using Dprental.AnalyticsService.Common.Enums;

namespace Dprental.AnalyticsService.Domain.Entities;

/// <summary>
/// Một dòng read model cho một dòng thuê: một thiết bị của một nhà cung cấp trong đơn.
/// </summary>
public sealed class RentalItemFact
{
    public Guid RentalItemId { get; set; } // Khóa chính
    public Guid OrderId { get; set; }      // Đơn chứa dòng này; mọi event item đều có rentalOrderId

    // Snapshot thiết bị từ RentalOrderCreated.items[] → nullable (dòng tạm, hoặc snapshot gốc thiếu)
    public Guid? SupplierId { get; set; }          // Nhà cung cấp → mọi KPI nhóm NCC
    public Guid? EquipmentListingId { get; set; }  // Tin đăng → TopListings
    public Guid? EquipmentTypeId { get; set; }     // Loại máy, dùng để nhóm → TopEquipmentTypes
    public string? EquipmentTypeName { get; set; } // Tên loại máy, chỉ để hiển thị
    public Guid? BrandId { get; set; }             // Hãng, dùng để nhóm → TopBrands
    public string? BrandName { get; set; }         // Tên hãng, chỉ để hiển thị
    public string? Model { get; set; }             // Model máy, chỉ để hiển thị
    public int? Quantity { get; set; }             // Số máy trong dòng → RentedQuantity
    public long? RentalAmount { get; set; }        // Tiền thuê (VND) → GMV, CompletedRentalValue, RentalValue
    public long? DepositAmount { get; set; }       // Tiền cọc (VND) → DepositCollected; không phải doanh thu
    public DateOnly? StartDate { get; set; }       // Ngày bắt đầu thuê dự kiến
    public DateOnly? EndDate { get; set; }         // Ngày kết thúc thuê dự kiến

    // Mỗi mốc do một event riêng điền; null khi mốc đó chưa (hoặc không bao giờ) xảy ra
    public DateTimeOffset? PaidAt { get; set; }      // RentalOrderPaid.paidAt → GMV, RentalItemCount, Top
    public DateTimeOffset? ConfirmedAt { get; set; } // RentalItemConfirmed.decisionAt → AcceptedCount
    public DateTimeOffset? RejectedAt { get; set; }  // RentalItemRejected.decisionAt → RejectedCount
    public DateTimeOffset? ExpiredAt { get; set; }   // RentalItemExpired.expiredAt → ConfirmationTimeoutCount
    public string? ExpiryReason { get; set; }        // Lý do hết hạn, ví dụ SUPPLIER_CONFIRMATION_EXPIRED
    public DateTimeOffset? CancelledAt { get; set; } // RentalItemCancelled.cancelledAt
    public DateTimeOffset? HandoverAt { get; set; }  // RentalItemHandoverCompleted.actualHandoverAt → RentedQuantity
    public DateTimeOffset? ReturnedAt { get; set; }  // RentalItemReturned.returnsCompletedAt
    public DateTimeOffset? CompletedAt { get; set; } // RentalItemCompleted → CompletedRentalValue, CompletedRentalCount

    public ItemFactStatus Status { get; set; } // Trạng thái hiện tại của dòng thuê

    // orderVersion (version của ĐƠN) của event cuối đã đổi Status; chặn event cũ kéo trạng thái lùi
    public long? LastOrderVersion { get; set; }

    // Ánh xạ tới cột hệ thống xmin của PostgreSQL để chống ghi đè đồng thời
    public uint RowVersion { get; set; }
}
