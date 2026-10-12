using Dprental.RentalService.Common;

namespace Dprental.RentalService.Domain.Errors;

public static class RentalErrors
{
    // Cart Errors
    public static readonly Error CartNotFound =
        Error.NotFound("Cart.NotFound", "Không tìm thấy giỏ thuê.");

    public static readonly Error CartNotActive =
        Error.Conflict("Cart.NotActive", "Giỏ thuê không ở trạng thái hoạt động.");

    public static readonly Error CartItemNotFound =
        Error.NotFound("Cart.ItemNotFound", "Không tìm thấy dòng thiết bị trong giỏ thuê.");

    public static readonly Error CartEmpty =
        Error.Validation("Cart.Empty", "Giỏ thuê không có sản phẩm nào để checkout.");
    public static readonly Error CannotRentOwnListing =
        Error.Forbidden("Cart.CannotRentOwnListing", "Người thuê không thể thuê thiết bị do chính mình sở hữu.");
    public static readonly Error OwnerIdRequired =
        Error.Validation("Cart.OwnerIdRequired", "Bắt buộc phải cung cấp OwnerId của thiết bị.");
    public static readonly Error QuantityOverflow =
        Error.Validation("Cart.QuantityOverflow", "Tổng số lượng vượt quá giới hạn cho phép.");

    public static readonly Error InvalidQuantity =
        Error.Validation("Rental.InvalidQuantity", "Số lượng thuê phải lớn hơn 0.");

    public static readonly Error InvalidRentalPeriod =
        Error.Validation("Rental.InvalidPeriod", "Ngày bắt đầu không được sau ngày kết thúc.");
    public static readonly Error DurationExceedsOneYear =
        Error.Validation("Rental.DurationExceedsOneYear", "Thời gian thuê không được vượt quá 1 năm.");

    // Order Errors
    public static readonly Error OrderNotFound =
        Error.NotFound("Order.NotFound", "Không tìm thấy đơn thuê.");

    public static readonly Error InvalidOrderStatus =
        Error.Conflict("Order.InvalidStatus", "Trạng thái đơn thuê không hợp lệ cho thao tác này.");

    public static readonly Error OrderAlreadyPaid =
        Error.Conflict("Order.AlreadyPaid", "Đơn thuê đã được xác nhận thanh toán trước đó.");

    public static readonly Error PaymentExpired =
        Error.Expired("Order.PaymentExpired", "Thời hạn thanh toán của đơn thuê đã hết.");

    public static readonly Error PaymentNotConfirmed =
        Error.Conflict("Order.PaymentNotConfirmed", "Đơn thuê chưa được thanh toán thành công.");

    public static readonly Error OrderCancellationForbidden =
        Error.Conflict("Order.CancellationForbidden", "Không thể hủy đơn thuê ở trạng thái hiện tại.");

    public static readonly Error PaymentDeadlineNotReached =
        Error.Validation("Order.PaymentDeadlineNotReached", "Chưa đến thời hạn hết hạn thanh toán.");

    public static readonly Error OperationConflict =
        Error.Conflict("Operation.Conflict", "OperationId đã được sử dụng cho một thao tác khác.");

    public static readonly Error OperationAlreadyProcessed =
        Error.Conflict("Operation.AlreadyProcessed", "Thao tác đã được thực hiện trước đó.");

    public static readonly Error DuplicateId =
        Error.Conflict("Rental.DuplicateId", "Mã định danh bị trùng lặp.");

    public static readonly Error StringLengthExceeded =
        Error.Validation("Rental.StringLengthExceeded", "Độ dài chuỗi vượt quá giới hạn quy định.");
    // Item Errors
    public static readonly Error ItemNotFound =
        Error.NotFound("RentalItem.NotFound", "Không tìm thấy khoản thuê.");

    public static readonly Error InvalidItemStatus =
        Error.Conflict("RentalItem.InvalidStatus", "Trạng thái khoản thuê không hợp lệ cho thao tác này.");

    public static readonly Error SupplierMismatch =
        Error.Forbidden("RentalItem.SupplierMismatch", "Supplier không sở hữu khoản thuê này.");

    public static readonly Error SupplierDeadlineExpired =
        Error.Expired("RentalItem.SupplierDeadlineExpired", "Đã hết thời hạn xác nhận của supplier.");

    public static readonly Error SupplierDecisionAlreadyMade =
        Error.Conflict("RentalItem.SupplierDecisionAlreadyMade", "Supplier đã đưa ra quyết định cho khoản thuê này.");

    public static readonly Error SupplierDeadlineNotExpired =
        Error.Validation("RentalItem.SupplierDeadlineNotExpired", "Chưa đến thời hạn hết hạn xác nhận của supplier.");

    public static readonly Error InventoryConfirmationRequired =
        Error.Validation("RentalItem.InventoryConfirmationRequired", "Yêu cầu mã xác nhận phân bổ từ Inventory trước khi chấp nhận.");

    public static readonly Error ClosureReceiptMissing =
        Error.Conflict("RentalItem.ClosureReceiptMissing", "Chưa nhận đủ xác nhận đóng từ Inventory hoặc Payment.");


    public static readonly Error DamageDispositionMissing =
        Error.Conflict("RentalItem.DamageDispositionMissing", "Thiết bị hư hỏng yêu cầu mã xác nhận phong tỏa/xử lý từ Inventory.");

    public static readonly Error SettlementAmountMismatch =
        Error.Validation("Payment.SettlementAmountMismatch", "Số tiền quyết toán không khớp với quyết định khấu trừ đã chốt.");

    public static readonly Error InvalidTimeout =
        Error.Validation("Payment.InvalidTimeout", "Thời hạn xác nhận phải là khoảng thời gian dương.");

    public static readonly Error InvoiceMismatch =
        Error.Conflict("Payment.InvoiceMismatch", "Mã hóa đơn không khớp với hóa đơn đã tạo của đơn thuê.");

    public static readonly Error PaymentAmountMismatch =
        Error.Validation("Payment.AmountMismatch", "Số tiền thanh toán không khớp với tổng tiền đơn thuê.");

    public static readonly Error UnitNotReturned =
        Error.Conflict("RentalItem.UnitNotReturned", "Không thể đóng phân bổ unit chưa được ghi nhận trả hàng.");
    public static readonly Error InvoiceNotAttached =
        Error.Conflict("Payment.InvoiceNotAttached", "Đơn thuê chưa được gắn mã hóa đơn từ Payment.");

    public static readonly Error RenterMismatch =
        Error.Forbidden("Order.RenterMismatch", "Người thanh toán không khớp với người đặt đơn thuê.");

    public static readonly Error PaymentRefMismatch =
        Error.Conflict("Payment.PaymentRefMismatch", "Mã thanh toán không khớp với thanh toán đã ghi nhận của đơn thuê.");

    public static readonly Error AlreadyPaidWithDifferentRef =
        Error.Conflict("Payment.AlreadyPaidWithDifferentRef", "Đơn thuê đã được thanh toán với mã thanh toán khác.");

    public static readonly Error InvalidSettlementTime =
        Error.Validation("Payment.InvalidSettlementTime", "Thời gian xác nhận quyết toán không thể diễn ra trước thời điểm hoàn tất trả hàng.");

    public static readonly Error SettlementImmutable =
        Error.Conflict("Payment.SettlementImmutable", "Xác nhận quyết toán tài chính đã được ghi nhận trước đó và không thể thay đổi.");

    public static readonly Error AllocationAlreadyClosed =
        Error.Conflict("RentalItem.AllocationAlreadyClosed", "Phân bổ của unit đã được đóng với thông tin khác.");

    public static readonly Error InventoryClosureImmutable =
        Error.Conflict("RentalItem.InventoryClosureImmutable", "Xác nhận đóng từ Inventory đã được ghi nhận trước đó và không thể thay đổi.");
    public static readonly Error AllocationNotConfirmed =
        Error.Conflict("RentalItem.AllocationNotConfirmed", "Phân bổ thiết bị chưa được xác nhận.");
    // Assignment & Unit Errors
    public static readonly Error AssignmentNotFound =
        Error.NotFound("RentalItem.AssignmentNotFound", "Không tìm thấy phân bổ unit cho khoản thuê.");

    public static readonly Error QuantityAssignmentMismatch =
        Error.Validation("RentalItem.QuantityMismatch", "Số lượng unit phân bổ không khớp với số lượng yêu cầu.");

    public static readonly Error DuplicateAssignment =
        Error.Conflict("RentalItem.DuplicateAssignment", "Tài sản hoặc phân bổ bị trùng lặp.");

    public static readonly Error InvalidAssignmentStatus =
        Error.Conflict("RentalItem.InvalidAssignmentStatus", "Trạng thái phân bổ unit không hợp lệ cho thao tác này.");

    public static readonly Error AssignmentAlreadyReturned =
        Error.Conflict("RentalItem.AssignmentAlreadyReturned", "Unit này đã được ghi nhận trả.");

    // Handover Errors
    public static readonly Error HandoverAlreadySubmitted =
        Error.Conflict("RentalItem.HandoverAlreadySubmitted", "Biên bản bàn giao đã được gửi trước đó.");

    public static readonly Error IncompleteHandover =
        Error.Validation("RentalItem.IncompleteHandover", "Biên bản bàn giao phải ghi nhận đầy đủ tất cả các unit của khoản thuê.");

    public static readonly Error InvalidHandoverActor =
        Error.Forbidden("RentalItem.InvalidHandoverActor", "Chỉ supplier sở hữu khoản thuê mới được gửi biên bản bàn giao.");

    public static readonly Error InvalidHandoverTime =
        Error.Validation("RentalItem.InvalidHandoverTime", "Thời gian bàn giao không hợp lệ.");
    // Return Errors
    public static readonly Error ReturnAlreadySubmitted =
        Error.Conflict("RentalItem.ReturnAlreadySubmitted", "Tất cả unit của khoản thuê đã được trả đủ.");

    public static readonly Error UnitNotHandedOver =
        Error.Conflict("RentalItem.UnitNotHandedOver", "Không thể trả unit chưa được bàn giao.");

    public static readonly Error InvalidReturnActor =
        Error.Forbidden("RentalItem.InvalidReturnActor", "Chỉ supplier sở hữu khoản thuê mới được ghi nhận trả hàng.");

    public static readonly Error IncompleteReturn =
        Error.Validation("RentalItem.IncompleteReturn", "Biên bản trả hàng phải ghi nhận ít nhất một unit.");
    public static readonly Error InvalidReturnTime =
        Error.Validation("RentalItem.InvalidReturnTime", "Thời gian trả hàng không thể trước thời gian bàn giao.");

    // Deduction Errors
    public static readonly Error DeductionDeadlineExpired =
        Error.Expired("Deduction.DeadlineExpired", "Đã hết thời hạn 48 giờ để supplier gửi đề xuất khấu trừ.");

    public static readonly Error DeductionAlreadySubmitted =
        Error.Conflict("Deduction.AlreadySubmitted", "Đề xuất khấu trừ đã được tạo cho khoản thuê này.");

    public static readonly Error DeductionAmountExceedsDeposit =
        Error.Validation("Deduction.AmountExceedsDeposit", "Tổng số tiền khấu trừ đề xuất không được vượt quá số tiền cọc.");

    public static readonly Error ProposalNotFound =
        Error.NotFound("Deduction.ProposalNotFound", "Không tìm thấy đề xuất khấu trừ.");

    public static readonly Error ProposalNotSubmitted =
        Error.Conflict("Deduction.NotSubmitted", "Đề xuất khấu trừ không ở trạng thái chờ phản hồi.");

    public static readonly Error ProposalAlreadyResponded =
        Error.Conflict("Deduction.AlreadyResponded", "Đề xuất khấu trừ đã được phản hồi.");

    public static readonly Error ResponseDeadlineExpired =
        Error.Expired("Deduction.ResponseDeadlineExpired", "Đã hết thời hạn 48 giờ phản hồi đề xuất khấu trừ.");

    public static readonly Error DeductionDeadlineNotExpired =
        Error.Validation("Deduction.DeadlineNotExpired", "Chưa đến thời hạn 48 giờ để supplier gửi đề xuất khấu trừ.");

    public static readonly Error ResponseDeadlineNotExpired =
        Error.Validation("Deduction.ResponseDeadlineNotExpired", "Chưa đến thời hạn 48 giờ phản hồi đề xuất khấu trừ.");

    public static readonly Error ProposalPendingResponse =
        Error.Conflict("Deduction.ProposalPendingResponse", "Đề xuất khấu trừ đang chờ renter phản hồi.");

    public static readonly Error DeductionNotFinalized =
        Error.Conflict("Deduction.NotFinalized", "Khoản khấu trừ chưa được chốt.");
    // Dispute Errors
    public static readonly Error DisputeAlreadyExists =
        Error.Conflict("Dispute.AlreadyExists", "Khiếu nại đã được mở cho đề xuất khấu trừ này.");

    public static readonly Error DisputeNotFound =
        Error.NotFound("Dispute.NotFound", "Không tìm thấy khiếu nại.");

    public static readonly Error DisputeNotOpen =
        Error.Conflict("Dispute.NotOpen", "Khiếu nại không ở trạng thái mở.");

    public static readonly Error DisputeAlreadyResolved =
        Error.Conflict("Dispute.AlreadyResolved", "Khiếu nại đã được giải quyết.");

    public static readonly Error DisputeBlocksCompletion =
        Error.Conflict("Dispute.BlocksCompletion", "Khiếu nại đang mở chặn việc hoàn tất khoản thuê.");

    public static readonly Error AdminOnlyResolution =
        Error.Forbidden("Dispute.AdminOnly", "Chỉ Admin mới có quyền giải quyết khiếu nại.");

    public static readonly Error ResolutionExceedsProposedAmount =
        Error.Validation("Dispute.ResolutionExceedsProposal", "Số tiền khấu trừ chốt không được vượt quá đề xuất của supplier.");

    public static readonly Error ResolutionExceedsDeposit =
        Error.Validation("Dispute.ResolutionExceedsDeposit", "Số tiền khấu trừ chốt không được vượt quá tiền cọc.");

    // Snapshot & Money Errors
    public static readonly Error CurrencyMismatch =
        Error.Validation("Money.CurrencyMismatch", "Tiền tệ không trùng khớp trong cùng giao dịch.");

    public static readonly Error NegativeAmount =
        Error.Validation("Money.NegativeAmount", "Số tiền không được là số âm.");

    public static readonly Error InvalidSnapshot =
        Error.Validation("Snapshot.Invalid", "Thông tin snapshot không được để trống hoặc vi phạm quy định.");

    public static readonly Error SnapshotContainsInternalData =
        Error.Validation("Snapshot.ContainsInternalData", "Snapshot thương mại không được chứa mã tài sản nội bộ hoặc số serial.");
}
