using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Refunds;

/// <summary>
/// Tình trạng một khoản hoàn trong danh sách của Admin.
/// </summary>
public enum AdminRefundQueueState
{
    /// <summary>Chờ hoàn: Admin chưa xác nhận đã chuyển khoản.</summary>
    Pending = 0,

    /// <summary>Admin đã xác nhận chuyển khoản.</summary>
    Completed = 1
}

/// <summary>
/// Admin xác nhận đã chuyển khoản hoàn tiền cho người thuê.
/// </summary>
/// <param name="BankTransactionRef">Mã giao dịch trên sao kê ngân hàng (bắt buộc, tối đa 64 ký tự, mỗi mã chỉ dùng một lần).</param>
/// <param name="Note">Ghi chú của Admin, tùy chọn, tối đa 500 ký tự.</param>
public sealed record ConfirmRefundTransferRequest(
    string? BankTransactionRef,
    string? Note);

/// <summary>
/// Xác nhận chuyển khoản hoàn tiền của Admin.
/// </summary>
/// <param name="RefundAmount">Số tiền đã chuyển.</param>
/// <param name="RefundAccount">Tài khoản đã chuyển vào, chép lúc xác nhận; rỗng với đơn cũ chưa lưu tài khoản.</param>
/// <param name="BankTransactionRef">Mã giao dịch trên sao kê ngân hàng.</param>
/// <param name="Note">Ghi chú của Admin.</param>
/// <param name="ConfirmedBy">Admin xác nhận.</param>
/// <param name="ConfirmedAt">Thời điểm xác nhận.</param>
public sealed record AdminRefundConfirmationResponse(
    decimal RefundAmount,
    RenterRefundAccountResponse? RefundAccount,
    string BankTransactionRef,
    string? Note,
    Guid ConfirmedBy,
    DateTimeOffset ConfirmedAt);

/// <summary>
/// Một khoản hoàn trong mục Hoàn tiền của chi tiết đơn Admin.
/// </summary>
/// <param name="RentalItemId">Mã dòng thiết bị.</param>
/// <param name="EquipmentName">Tên thiết bị tại thời điểm đặt.</param>
/// <param name="SupplierId">Nhà cung cấp của dòng.</param>
/// <param name="ItemStatus">Trạng thái hiện tại của dòng.</param>
/// <param name="Kind">Hoàn toàn bộ hay hoàn phần cọc còn lại.</param>
/// <param name="RentalAmount">Tiền thuê của dòng.</param>
/// <param name="DepositAmount">Tiền cọc của dòng.</param>
/// <param name="DeductionAmount">Tiền khấu trừ cọc đã chốt; 0 với khoản hoàn toàn bộ.</param>
/// <param name="RefundAmount">Số tiền cần chuyển cho người thuê.</param>
/// <param name="RefundAccount">Tài khoản nhận hoàn tiền đủ số: tài khoản đã chuyển khi đã xác nhận, còn không là tài khoản người thuê khai lúc đặt hàng; rỗng với đơn cũ.</param>
/// <param name="State">Chờ hoàn hay đã hoàn.</param>
/// <param name="PaymentRequestStatus">Tình trạng gửi yêu cầu sang PaymentService, ví dụ Pending, Succeeded, MANUAL_REFUND_REQUIRED.</param>
/// <param name="RequestedAt">Thời điểm Rental tạo yêu cầu hoàn.</param>
/// <param name="Confirmation">Xác nhận chuyển khoản; rỗng khi chưa hoàn.</param>
public sealed record AdminOrderRefundResponse(
    Guid RentalItemId,
    string EquipmentName,
    Guid SupplierId,
    RentalItemStatus ItemStatus,
    RentalRefundKind Kind,
    decimal RentalAmount,
    decimal DepositAmount,
    decimal DeductionAmount,
    decimal RefundAmount,
    RenterRefundAccountResponse? RefundAccount,
    AdminRefundQueueState State,
    string PaymentRequestStatus,
    DateTimeOffset RequestedAt,
    AdminRefundConfirmationResponse? Confirmation);

/// <summary>
/// Một khoản cần hoàn trong menu Đơn yêu cầu hoàn tiền của Admin.
/// </summary>
/// <param name="RentalItemId">Mã dòng thiết bị, dùng để xác nhận chuyển khoản.</param>
/// <param name="OrderId">Mã đơn hàng, dùng để mở chi tiết đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị, ví dụ DH-2026-000123.</param>
/// <param name="RenterId">Người thuê.</param>
/// <param name="RenterName">Tên người nhận khai lúc đặt hàng.</param>
/// <param name="RenterPhone">Số điện thoại người nhận khai lúc đặt hàng.</param>
/// <param name="EquipmentName">Tên thiết bị tại thời điểm đặt.</param>
/// <param name="SupplierId">Nhà cung cấp của dòng.</param>
/// <param name="ItemStatus">Trạng thái hiện tại của dòng.</param>
/// <param name="Kind">Hoàn toàn bộ hay hoàn phần cọc còn lại.</param>
/// <param name="RentalAmount">Tiền thuê của dòng.</param>
/// <param name="DepositAmount">Tiền cọc của dòng.</param>
/// <param name="DeductionAmount">Tiền khấu trừ cọc đã chốt; 0 với khoản hoàn toàn bộ.</param>
/// <param name="RefundAmount">Số tiền cần chuyển cho người thuê.</param>
/// <param name="RefundAccount">Tài khoản nhận hoàn tiền đủ số; rỗng với đơn cũ chưa lưu tài khoản.</param>
/// <param name="State">Chờ hoàn hay đã hoàn.</param>
/// <param name="PaymentRequestStatus">Tình trạng gửi yêu cầu sang PaymentService, ví dụ Pending, Succeeded, MANUAL_REFUND_REQUIRED.</param>
/// <param name="RequestedAt">Thời điểm Rental tạo yêu cầu hoàn.</param>
/// <param name="Confirmation">Xác nhận chuyển khoản; rỗng khi chưa hoàn.</param>
public sealed record AdminRefundQueueItemResponse(
    Guid RentalItemId,
    Guid OrderId,
    string OrderCode,
    Guid RenterId,
    string RenterName,
    string RenterPhone,
    string EquipmentName,
    Guid SupplierId,
    RentalItemStatus ItemStatus,
    RentalRefundKind Kind,
    decimal RentalAmount,
    decimal DepositAmount,
    decimal DeductionAmount,
    decimal RefundAmount,
    RenterRefundAccountResponse? RefundAccount,
    AdminRefundQueueState State,
    string PaymentRequestStatus,
    DateTimeOffset RequestedAt,
    AdminRefundConfirmationResponse? Confirmation);
