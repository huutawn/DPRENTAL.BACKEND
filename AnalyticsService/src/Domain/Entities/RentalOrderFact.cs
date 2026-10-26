using Dprental.AnalyticsService.Common.Enums;

namespace Dprental.AnalyticsService.Domain.Entities;

/// <sumary>
///  Một dòng read model cho một đơn thuê. Chỉ chứa dữ liệu, không chứa quy tắc nghiệp vụ.
/// </sumary>
/// 

public sealed class RentalOrderFact
{
	public Guid OrderId { get; set; }

	// Các cột chỉ có trong RentalOrderCreated → nullable (dòng tạm khi event đến sai thứ tự)
	public string? OrderCode { get; set; } //mã đơn hàng
	public Guid? RenterId { get; set; } //mã người thuê
	public long? RentalAmount { get; set; } // Số tiền thuê
	public long? DepositAmount { get; set; } // Tiền cọc
	public int? ItemCount { get; set; } // số dòng thuê
	public int? SupplierCount { get; set; } // Số lượng nhà cung cấp khác nhau

	public DateTimeOffset? CreatedAt { get; set; } //thời gian tạo
	public DateTimeOffset? PaidAt { get; set; } //thời gian thanh toán
	public DateTimeOffset? CompletedAt { get; set; } // thời điểm đơn thuê hoàn tất toàn bộ vòng đời
	public DateTimeOffset? CancelledAt { get; set; } // thời điểm đơn hàng bị hủy vĩnh viễn
	public DateTimeOffset? ExpiredAt { get; set; }// thời điểm đơn bị chuyển sang trạng thái Hết hạn
	public DateTimeOffset? RejectedAt { get; set; } // tất cả các item trong đơn đều bị NCC từ chối

	public OrderFactStatus Status { get; set; } //trạng thái thực tế của đơn hàng
	public long? LastVersion { get; set; } // Phiên bản nghiệp vụ từ Kafka

	// Ánh xạ tới cột hệ thống xmin của PostgreSQL để chống ghi đè đồng thời
	public uint RowVersion { get; set; } //Phiên bản dòng vật lý trong PostgreSQL
}