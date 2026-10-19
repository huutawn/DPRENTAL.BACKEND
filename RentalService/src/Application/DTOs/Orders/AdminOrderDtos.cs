using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Điều kiện Admin tìm đơn; ô bỏ trống là không lọc. Các điều kiện theo dòng (rentalItemId, listingId, assetId,
/// supplierId, itemStatus) phải cùng khớp trên một dòng của đơn.
/// </summary>
public sealed class AdminOrderSearchRequest
{
    /// <summary>Mã đơn, ví dụ DH-2026-000001.</summary>
    public string? OrderCode { get; init; }

    /// <summary>Mã đơn con, ví dụ DH-2026-000001-01.</summary>
    public string? SubOrderCode { get; init; }

    /// <summary>Mã định danh dòng thiết bị.</summary>
    public Guid? RentalItemId { get; init; }

    /// <summary>Thiết bị đã đặt (EquipmentListingId).</summary>
    public Guid? ListingId { get; init; }

    /// <summary>Máy cụ thể Inventory đã gán.</summary>
    public Guid? AssetId { get; init; }

    /// <summary>Nhà cung cấp.</summary>
    public Guid? SupplierId { get; init; }

    /// <summary>Người thuê.</summary>
    public Guid? RenterId { get; init; }

    /// <summary>Trạng thái đơn, ví dụ PAID.</summary>
    public string? Status { get; init; }

    /// <summary>Trạng thái dòng, ví dụ IN_RENTAL.</summary>
    public string? ItemStatus { get; init; }

    /// <summary>Đặt hàng từ thời điểm này (gồm), ISO 8601 có Z hoặc ±HH:mm.</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>Đặt hàng trước thời điểm này (không gồm), ISO 8601 có Z hoặc ±HH:mm.</summary>
    public string? CreatedTo { get; init; }

    /// <summary>Số thứ tự trang, mặc định 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Số đơn mỗi trang, mặc định 20, tối đa 100.</summary>
    public int PageSize { get; init; } = 20;
}

/// <summary>
/// Một đơn trong kết quả tìm kiếm của Admin.
/// </summary>
/// <param name="OrderId">Mã định danh đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị.</param>
/// <param name="Status">Trạng thái đơn.</param>
/// <param name="RenterId">Người thuê.</param>
/// <param name="SupplierIds">Các nhà cung cấp trong đơn, theo thứ tự đơn con.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="TotalRentalAmount">Tổng tiền thuê.</param>
/// <param name="TotalDepositAmount">Tổng tiền cọc.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc.</param>
/// <param name="ItemCount">Số dòng thiết bị.</param>
/// <param name="InvoiceRef">Mã hóa đơn bên PaymentService.</param>
/// <param name="PaidAt">Thời điểm thanh toán.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
public sealed record AdminOrderSummaryResponse(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    Guid RenterId,
    IReadOnlyList<Guid> SupplierIds,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    int ItemCount,
    string? InvoiceRef,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Tài khoản nhận hoàn tiền của người thuê; số tài khoản chỉ hiện 4 số cuối.
/// </summary>
/// <param name="BankCode">Mã ngân hàng.</param>
/// <param name="MaskedAccountNumber">Số tài khoản đã che, ví dụ ******6789.</param>
/// <param name="AccountHolderName">Tên chủ tài khoản.</param>
public sealed record AdminRefundAccountResponse(
    string BankCode,
    string MaskedAccountNumber,
    string AccountHolderName);

/// <summary>
/// Một máy cụ thể Inventory đã gán cho dòng, kèm mã giữ lịch.
/// </summary>
/// <param name="AssignmentId">Mã gán máy.</param>
/// <param name="AssetId">Máy cụ thể.</param>
/// <param name="AllocationId">Mã giữ lịch bên Inventory.</param>
/// <param name="Status">Tình trạng của máy trong dòng.</param>
/// <param name="HandoverCondition">Tình trạng lúc giao.</param>
/// <param name="ReturnCondition">Tình trạng lúc trả.</param>
/// <param name="ReturnNote">Ghi chú lúc trả máy này.</param>
/// <param name="InventoryDispositionRef">Mã xử lý máy bên Inventory sau khi trả (nhả lịch hoặc khóa bảo trì).</param>
public sealed record AdminUnitResponse(
    Guid AssignmentId,
    Guid AssetId,
    Guid AllocationId,
    RentalUnitAssignmentStatus Status,
    UnitCondition? HandoverCondition,
    UnitCondition? ReturnCondition,
    string? ReturnNote,
    string? InventoryDispositionRef);

/// <summary>
/// Một dòng thiết bị với đầy đủ mã nội bộ để đối chiếu với Inventory và PaymentService.
/// </summary>
/// <param name="Name">Tên thiết bị lúc đặt hàng.</param>
/// <param name="Quantity">Số máy thuê.</param>
/// <param name="StartDate">Ngày đầu tiên dùng máy.</param>
/// <param name="EndDate">Ngày cuối cùng dùng máy (có tính).</param>
/// <param name="RentalItemId">Mã định danh dòng.</param>
/// <param name="EquipmentListingId">Thiết bị đã đặt.</param>
/// <param name="SupplierId">Nhà cung cấp.</param>
/// <param name="Status">Trạng thái của dòng.</param>
/// <param name="RentalDays">Số ngày thuê.</param>
/// <param name="DailyRate">Giá thuê một máy một ngày.</param>
/// <param name="DepositPerUnit">Tiền cọc một máy.</param>
/// <param name="RentalAmount">Tiền thuê.</param>
/// <param name="DepositAmount">Tiền cọc.</param>
/// <param name="AllocationGroupId">Nhóm giữ lịch bên Inventory.</param>
/// <param name="InventoryConfirmationRef">Mã Inventory xác nhận giữ lịch khi nhà cung cấp nhận.</param>
/// <param name="SupplierConfirmationDeadline">Hạn nhà cung cấp xác nhận.</param>
/// <param name="SupplierDecisionAt">Thời điểm nhà cung cấp quyết định.</param>
/// <param name="SupplierDecisionBy">Người quyết định.</param>
/// <param name="SupplierDecisionReason">Lý do (khi từ chối hoặc hết hạn).</param>
/// <param name="SupplierDecisionNote">Ghi chú của nhà cung cấp.</param>
/// <param name="Refund">Tình trạng hoàn tiền và RefundId bên PaymentService.</param>
/// <param name="SettlementRef">Mã quyết toán bên PaymentService.</param>
/// <param name="Handover">Biên bản bàn giao.</param>
/// <param name="Returns">Các lần trả máy.</param>
/// <param name="Settlement">Quyết toán tiền cọc.</param>
/// <param name="Units">Các máy cụ thể đã gán.</param>
public sealed record AdminOrderItemResponse(
    string Name,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    Guid RentalItemId,
    Guid EquipmentListingId,
    Guid SupplierId,
    RentalItemStatus Status,
    int RentalDays,
    decimal DailyRate,
    decimal DepositPerUnit,
    decimal RentalAmount,
    decimal DepositAmount,
    string AllocationGroupId,
    string? InventoryConfirmationRef,
    DateTimeOffset? SupplierConfirmationDeadline,
    DateTimeOffset? SupplierDecisionAt,
    Guid? SupplierDecisionBy,
    string? SupplierDecisionReason,
    string? SupplierDecisionNote,
    OrderItemRefundResponse Refund,
    string? SettlementRef,
    OrderHandoverResponse? Handover,
    IReadOnlyList<OrderReturnResponse> Returns,
    OrderSettlementResponse? Settlement,
    IReadOnlyList<AdminUnitResponse> Units);

/// <summary>
/// Một đơn con trong chi tiết đơn của Admin.
/// </summary>
/// <param name="SubOrderId">Mã định danh đơn con.</param>
/// <param name="SubOrderCode">Mã đơn con hiển thị.</param>
/// <param name="SupplierId">Nhà cung cấp.</param>
/// <param name="Status">Trạng thái đơn con, tính từ các dòng.</param>
/// <param name="TotalRentalAmount">Tiền thuê của đơn con.</param>
/// <param name="TotalDepositAmount">Tiền cọc của đơn con.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc của đơn con.</param>
/// <param name="Items">Các dòng thiết bị.</param>
public sealed record AdminSubOrderResponse(
    Guid SubOrderId,
    string SubOrderCode,
    Guid SupplierId,
    RentalItemStatus Status,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<AdminOrderItemResponse> Items);

/// <summary>
/// Một lần chuyển trạng thái của đơn hoặc của một dòng.
/// </summary>
/// <param name="RentalItemId">Dòng bị đổi trạng thái; rỗng khi là trạng thái của đơn.</param>
/// <param name="FromState">Trạng thái trước, rỗng khi vừa tạo.</param>
/// <param name="ToState">Trạng thái sau.</param>
/// <param name="Action">Hành động gây ra thay đổi.</param>
/// <param name="ActorId">Người thực hiện, rỗng khi hệ thống tự làm.</param>
/// <param name="ActorRole">Vai trò người thực hiện.</param>
/// <param name="Reason">Lý do.</param>
/// <param name="OccurredAt">Thời điểm thay đổi.</param>
public sealed record AdminStateTransitionResponse(
    Guid? RentalItemId,
    string? FromState,
    string ToState,
    string Action,
    Guid? ActorId,
    ActorRole ActorRole,
    string? Reason,
    DateTimeOffset OccurredAt);

/// <summary>
/// Chi tiết đơn cho Admin: toàn bộ đơn con, dòng, máy, mã nội bộ và lịch sử trạng thái.
/// </summary>
/// <param name="OrderId">Mã định danh đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị.</param>
/// <param name="Status">Trạng thái đơn.</param>
/// <param name="RenterId">Người thuê.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
/// <param name="PaymentDeadline">Hạn thanh toán.</param>
/// <param name="InvoiceRef">Mã hóa đơn (InvoiceId) bên PaymentService.</param>
/// <param name="PaymentRef">Mã giao dịch thanh toán bên PaymentService.</param>
/// <param name="PaidAt">Thời điểm thanh toán.</param>
/// <param name="PaymentState">Tình trạng thanh toán.</param>
/// <param name="Delivery">Thông tin nhận máy.</param>
/// <param name="RefundAccount">Tài khoản nhận hoàn tiền đã che số; rỗng với đơn cũ.</param>
/// <param name="TotalRentalAmount">Tổng tiền thuê.</param>
/// <param name="TotalDepositAmount">Tổng tiền cọc.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc.</param>
/// <param name="SubOrders">Các đơn con.</param>
/// <param name="History">Lịch sử trạng thái của đơn và các dòng, cũ nhất trước.</param>
/// <param name="Version">Phiên bản dữ liệu của đơn hàng, tăng khi có thay đổi nghiệp vụ.</param>
public sealed record AdminOrderDetailResponse(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    Guid RenterId,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset PaymentDeadline,
    string? InvoiceRef,
    string? PaymentRef,
    DateTimeOffset? PaidAt,
    OrderPaymentState PaymentState,
    OrderDeliveryResponse Delivery,
    AdminRefundAccountResponse? RefundAccount,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<AdminSubOrderResponse> SubOrders,
    IReadOnlyList<AdminStateTransitionResponse> History,
    long Version);
