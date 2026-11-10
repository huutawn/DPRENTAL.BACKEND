using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Tình trạng thanh toán của đơn, suy ra từ trạng thái đơn và thời điểm Payment xác nhận.
/// </summary>
public enum OrderPaymentState
{
    /// <summary>Đơn đang chờ thanh toán.</summary>
    Pending = 0,

    /// <summary>Đã thanh toán đúng hạn.</summary>
    Paid = 1,

    /// <summary>Đơn đã đóng mà không có thanh toán.</summary>
    Expired = 2,

    /// <summary>Thanh toán đến sau hạn; toàn bộ số tiền được hoàn lại.</summary>
    LatePaymentRefund = 3
}

/// <summary>
/// Tình trạng hoàn tiền của một dòng thiết bị.
/// </summary>
public enum OrderRefundState
{
    /// <summary>Dòng không có khoản hoàn nào.</summary>
    None = 0,

    /// <summary>Dòng có khoản hoàn, chờ Admin chuyển khoản.</summary>
    Processing = 1,

    /// <summary>Admin đã xác nhận chuyển khoản hoàn tiền.</summary>
    Completed = 2
}

/// <summary>
/// Tình trạng hoàn tiền của cả đơn, tính từ các khoản hoàn của các dòng.
/// </summary>
public enum OrderRefundProgress
{
    /// <summary>Đơn không có khoản hoàn nào.</summary>
    None = 0,

    /// <summary>Còn khoản chờ Admin chuyển khoản.</summary>
    Pending = 1,

    /// <summary>Mọi khoản hoàn đã được chuyển khoản.</summary>
    Refunded = 2
}

/// <summary>
/// Tài khoản nhận hoàn tiền người thuê khai lúc đặt hàng.
/// </summary>
/// <param name="BankCode">Mã ngân hàng.</param>
/// <param name="AccountNumber">Số tài khoản.</param>
/// <param name="AccountHolderName">Tên chủ tài khoản.</param>
public sealed record RenterRefundAccountResponse(
    string BankCode,
    string AccountNumber,
    string AccountHolderName);

/// <summary>
/// Thông tin thiết bị đã chụp lúc đặt hàng. Không đọc lại Listing hiện tại; các trường không được chụp thì rỗng.
/// </summary>
/// <param name="Description">Mô tả thiết bị.</param>
/// <param name="ThumbnailMediaId">Ảnh đại diện.</param>
/// <param name="EquipmentTypeName">Loại thiết bị.</param>
/// <param name="BrandName">Hãng.</param>
/// <param name="Model">Model.</param>
/// <param name="SpecValues">Thông số kỹ thuật dạng object JSON.</param>
/// <param name="PickupLocation">Nơi nhận máy của nhà cung cấp.</param>
/// <param name="EquipmentTypeId">Mã loại thiết bị tại thời điểm đặt; đơn cũ có thể không có.</param>
/// <param name="BrandId">Mã thương hiệu tại thời điểm đặt; đơn cũ có thể không có.</param>
/// <param name="SpecSchemaVersion">Phiên bản schema thông số tại thời điểm đặt; đơn cũ có thể không có.</param>
public sealed record OrderListingSnapshotResponse(
    string? Description,
    Guid? ThumbnailMediaId,
    string? EquipmentTypeName,
    string? BrandName,
    string? Model,
    JsonElement? SpecValues,
    string? PickupLocation,
    Guid? EquipmentTypeId = null,
    Guid? BrandId = null,
    int? SpecSchemaVersion = null);

/// <summary>
/// Tình trạng hoàn tiền của một dòng.
/// </summary>
/// <param name="State">Chưa có, đang hoàn hoặc đã hoàn.</param>
/// <param name="RefundRef">Mã hoàn tiền bên PaymentService, nếu Payment đã gửi về.</param>
/// <param name="Kind">Hoàn toàn bộ tiền thuê và cọc, hay hoàn phần cọc còn lại; rỗng khi dòng không có khoản hoàn.</param>
/// <param name="RefundAmount">Số tiền được hoàn; rỗng khi dòng không có khoản hoàn.</param>
/// <param name="RefundedAt">Thời điểm Admin xác nhận đã chuyển khoản; rỗng khi chưa hoàn.</param>
public sealed record OrderItemRefundResponse(
    OrderRefundState State,
    string? RefundRef,
    RentalRefundKind? Kind = null,
    decimal? RefundAmount = null,
    DateTimeOffset? RefundedAt = null);

/// <summary>
/// Biên bản bàn giao của một dòng.
/// </summary>
/// <param name="ActualHandoverAt">Thời điểm nhà cung cấp giao máy.</param>
/// <param name="Note">Ghi chú lúc giao.</param>
/// <param name="RenterAcknowledgedAt">Thời điểm người thuê xác nhận đã nhận, rỗng khi chưa xác nhận.</param>
/// <param name="RenterAcknowledgedById">Mã người thuê đã xác nhận nhận máy, rỗng khi chưa xác nhận.</param>
/// <param name="RenterAcknowledgementNote">Ghi chú của người thuê lúc xác nhận nhận máy.</param>
public sealed record OrderHandoverResponse(
    DateTimeOffset ActualHandoverAt,
    string? Note,
    DateTimeOffset? RenterAcknowledgedAt,
    Guid? RenterAcknowledgedById = null,
    string? RenterAcknowledgementNote = null);

/// <summary>
/// Một lần nhà cung cấp ghi nhận trả máy.
/// </summary>
/// <param name="ActualReturnAt">Thời điểm trả máy.</param>
/// <param name="Note">Ghi chú lúc trả.</param>
/// <param name="NormalUnits">Số máy trả bình thường trong lần này.</param>
/// <param name="DamagedUnits">Số máy trả hư hỏng trong lần này.</param>
public sealed record OrderReturnResponse(
    DateTimeOffset ActualReturnAt,
    string? Note,
    int NormalUnits,
    int DamagedUnits);

/// <summary>
/// Quyết toán tiền cọc của một dòng sau khi trả đủ máy.
/// </summary>
/// <param name="ReturnsCompletedAt">Thời điểm trả đủ máy.</param>
/// <param name="DeductionDeadline">Hạn nhà cung cấp gửi đề xuất khấu trừ, chỉ có khi có máy hư hỏng.</param>
/// <param name="DeductionStatus">Trạng thái đề xuất khấu trừ, rỗng khi chưa có đề xuất.</param>
/// <param name="ProposedDeductionAmount">Số tiền nhà cung cấp đề xuất khấu trừ.</param>
/// <param name="RenterResponseDeadline">Hạn người thuê đồng ý hoặc khiếu nại đề xuất.</param>
/// <param name="FinalDeductionAmount">Số tiền khấu trừ đã chốt.</param>
/// <param name="DepositRefundAmount">Tiền cọc được hoàn = tiền cọc - khấu trừ đã chốt.</param>
/// <param name="SettledAt">Thời điểm PaymentService xác nhận quyết toán xong.</param>
public sealed record OrderSettlementResponse(
    DateTimeOffset? ReturnsCompletedAt,
    DateTimeOffset? DeductionDeadline,
    DeductionProposalStatus? DeductionStatus,
    decimal? ProposedDeductionAmount,
    DateTimeOffset? RenterResponseDeadline,
    decimal? FinalDeductionAmount,
    decimal? DepositRefundAmount,
    DateTimeOffset? SettledAt);

/// <summary>
/// Một dòng thiết bị trong đơn của người thuê. Giá và thông tin thiết bị là bản đã chụp lúc đặt hàng.
/// Không chứa máy cụ thể (AssetId), mã giữ lịch hay mã xác nhận nội bộ của Inventory.
/// </summary>
/// <param name="Name">Tên thiết bị lúc đặt hàng.</param>
/// <param name="Quantity">Số máy thuê.</param>
/// <param name="StartDate">Ngày đầu tiên dùng máy.</param>
/// <param name="EndDate">Ngày cuối cùng dùng máy (có tính), giống cách giỏ hàng hiển thị.</param>
/// <param name="RentalItemId">Mã định danh dòng.</param>
/// <param name="EquipmentListingId">Thiết bị đã đặt.</param>
/// <param name="Status">Trạng thái của dòng.</param>
/// <param name="Listing">Thông tin thiết bị đã chụp lúc đặt hàng.</param>
/// <param name="RentalDays">Số ngày thuê.</param>
/// <param name="DailyRate">Giá thuê một máy một ngày.</param>
/// <param name="DepositPerUnit">Tiền cọc một máy.</param>
/// <param name="RentalAmount">Tiền thuê = giá ngày x số máy x số ngày.</param>
/// <param name="DepositAmount">Tiền cọc = cọc một máy x số máy.</param>
/// <param name="SupplierConfirmationDeadline">Hạn nhà cung cấp xác nhận.</param>
/// <param name="SupplierDecisionAt">Thời điểm nhà cung cấp nhận hoặc từ chối.</param>
/// <param name="RejectReason">Lý do từ chối, chỉ có khi nhà cung cấp từ chối.</param>
/// <param name="Refund">Tình trạng hoàn tiền của dòng.</param>
/// <param name="Handover">Biên bản bàn giao, rỗng khi chưa giao.</param>
/// <param name="Returns">Các lần trả máy, cũ nhất trước.</param>
/// <param name="Settlement">Quyết toán tiền cọc, rỗng khi chưa trả đủ máy.</param>
/// <param name="Units">Danh sách máy cụ thể của dòng kèm trạng thái yêu cầu trả.</param>
/// <param name="ReturnRequests">Danh sách các yêu cầu trả thiết bị đã gửi.</param>
/// <param name="ReviewStatus">Trạng thái đánh giá của dòng thiết bị.</param>
public sealed record RenterOrderItemResponse(
    string Name,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    Guid RentalItemId,
    Guid EquipmentListingId,
    RentalItemStatus Status,
    OrderListingSnapshotResponse Listing,
    int RentalDays,
    decimal DailyRate,
    decimal DepositPerUnit,
    decimal RentalAmount,
    decimal DepositAmount,
    DateTimeOffset? SupplierConfirmationDeadline,
    DateTimeOffset? SupplierDecisionAt,
    string? RejectReason,
    OrderItemRefundResponse Refund,
    OrderHandoverResponse? Handover,
    IReadOnlyList<OrderReturnResponse> Returns,
    OrderSettlementResponse? Settlement,
    IReadOnlyList<RenterUnitResponse> Units,
    IReadOnlyList<ReturnRequestResponse> ReturnRequests,
    ReviewStatusDto ReviewStatus)
{
    public RenterOrderItemResponse(
        string name,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        Guid rentalItemId,
        Guid equipmentListingId,
        RentalItemStatus status,
        OrderListingSnapshotResponse listing,
        int rentalDays,
        decimal dailyRate,
        decimal depositPerUnit,
        decimal rentalAmount,
        decimal depositAmount,
        DateTimeOffset? supplierConfirmationDeadline,
        DateTimeOffset? supplierDecisionAt,
        string? rejectReason,
        OrderItemRefundResponse refund,
        OrderHandoverResponse? handover,
        IReadOnlyList<OrderReturnResponse> returns,
        OrderSettlementResponse? settlement,
        IReadOnlyList<RenterUnitResponse> units,
        IReadOnlyList<ReturnRequestResponse> returnRequests)
        : this(name, quantity, startDate, endDate, rentalItemId, equipmentListingId, status, listing,
               rentalDays, dailyRate, depositPerUnit, rentalAmount, depositAmount,
               supplierConfirmationDeadline, supplierDecisionAt, rejectReason, refund, handover, returns, settlement,
               units, returnRequests, ReviewStatusDto.Unknown)
    {
    }

    public RenterOrderItemResponse(
        string name,
        int quantity,
        DateOnly startDate,
        DateOnly endDate,
        Guid rentalItemId,
        Guid equipmentListingId,
        RentalItemStatus status,
        OrderListingSnapshotResponse listing,
        int rentalDays,
        decimal dailyRate,
        decimal depositPerUnit,
        decimal rentalAmount,
        decimal depositAmount,
        DateTimeOffset? supplierConfirmationDeadline,
        DateTimeOffset? supplierDecisionAt,
        string? rejectReason,
        OrderItemRefundResponse refund,
        OrderHandoverResponse? handover,
        IReadOnlyList<OrderReturnResponse> returns,
        OrderSettlementResponse? settlement)
        : this(name, quantity, startDate, endDate, rentalItemId, equipmentListingId, status, listing,
               rentalDays, dailyRate, depositPerUnit, rentalAmount, depositAmount,
               supplierConfirmationDeadline, supplierDecisionAt, rejectReason, refund, handover, returns, settlement,
               [], [], ReviewStatusDto.Unknown)
    {
    }
}

/// <summary>
/// Máy cụ thể trong dòng đơn mà người thuê xem: đủ thông tin để chọn máy cần trả, không để lộ dữ liệu nội bộ.
/// </summary>
/// <param name="AssignmentId">Mã gán máy, gửi vào API POST return-requests.</param>
/// <param name="Status">Trạng thái assignment hiện tại.</param>
/// <param name="HasOpenReturnRequest">Có request unit chưa đóng hay không.</param>
/// <param name="CanRequestReturn">Có được tạo yêu cầu trả mới tại thời điểm đọc hay không.</param>
public sealed record RenterUnitResponse(
    Guid AssignmentId,
    RentalUnitAssignmentStatus Status,
    bool HasOpenReturnRequest,
    bool CanRequestReturn);
