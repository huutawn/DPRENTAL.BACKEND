namespace Dprental.AnalyticsService.Common.Enums;

/// <summary>
/// Trạng thái của một dòng thuê trong read model, cùng tập giá trị với trạng thái item của Rental.
/// </summary>
public enum ItemFactStatus
{
    PendingPayment,              // Chờ thanh toán (RentalOrderCreated)
    PendingSupplierConfirmation, // Đã trả tiền, chờ NCC xác nhận (RentalOrderPaid / RentalItemPaid)
    AwaitingHandover,            // NCC đã nhận, chờ giao máy (RentalItemConfirmed)
    InRental,                    // Đã giao máy, đang thuê (RentalItemHandoverCompleted)
    PendingSettlement,           // Đã trả máy, chờ quyết toán (RentalItemReturned)
    Completed,                   // Hoàn thành (RentalItemCompleted)
    Rejected,                    // NCC từ chối (RentalItemRejected)
    Expired,                     // Hết hạn (RentalItemExpired)
    Cancelled                    // Bị hủy (RentalItemCancelled)
}
