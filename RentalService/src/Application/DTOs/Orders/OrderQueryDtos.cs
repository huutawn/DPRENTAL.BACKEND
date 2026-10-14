using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Orders;

/// <summary>
/// Một trang kết quả kèm thông tin phân trang.
/// </summary>
/// <typeparam name="T">Kiểu phần tử của trang.</typeparam>
/// <param name="Items">Các phần tử của trang hiện tại.</param>
/// <param name="Page">Số thứ tự trang hiện tại, bắt đầu từ 1.</param>
/// <param name="PageSize">Số phần tử tối đa mỗi trang.</param>
/// <param name="TotalCount">Tổng số phần tử khớp điều kiện.</param>
/// <param name="TotalPages">Tổng số trang.</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>
/// Thông tin nhận máy người thuê đã khai lúc đặt hàng.
/// </summary>
/// <param name="Method">Hình thức giao nhận.</param>
/// <param name="RecipientName">Tên người nhận.</param>
/// <param name="RecipientPhone">Số điện thoại người nhận.</param>
/// <param name="Address">Địa chỉ nhận máy.</param>
/// <param name="Note">Ghi chú giao nhận, có thể rỗng.</param>
public sealed record OrderDeliveryResponse(
    string Method,
    string RecipientName,
    string RecipientPhone,
    string Address,
    string? Note);

/// <summary>
/// Một dòng thiết bị trong đơn, lấy từ thông tin đã chụp lúc đặt hàng.
/// </summary>
/// <param name="RentalItemId">Mã dòng thuê.</param>
/// <param name="EquipmentListingId">Mã thiết bị niêm yết.</param>
/// <param name="ListingTitle">Tên thiết bị lúc đặt hàng.</param>
/// <param name="EquipmentTypeName">Tên loại thiết bị lúc đặt hàng.</param>
/// <param name="BrandName">Tên hãng lúc đặt hàng.</param>
/// <param name="Model">Model lúc đặt hàng.</param>
/// <param name="ThumbnailMediaId">Mã ảnh đại diện, có thể rỗng.</param>
/// <param name="PickupLocation">Nơi nhận máy của nhà cung cấp.</param>
/// <param name="Quantity">Số máy thuê.</param>
/// <param name="StartDate">Ngày đầu tiên dùng máy.</param>
/// <param name="EndDate">Ngày cuối cùng dùng máy (có tính), giống cách giỏ hàng hiển thị.</param>
/// <param name="RentalDays">Số ngày thuê.</param>
/// <param name="DailyRate">Giá thuê một máy một ngày.</param>
/// <param name="DepositPerUnit">Tiền cọc một máy.</param>
/// <param name="RentalAmount">Tiền thuê của dòng.</param>
/// <param name="DepositAmount">Tiền cọc của dòng.</param>
/// <param name="Status">Trạng thái của dòng.</param>
public sealed record OrderItemResponse(
    Guid RentalItemId,
    Guid EquipmentListingId,
    string ListingTitle,
    string EquipmentTypeName,
    string BrandName,
    string Model,
    Guid? ThumbnailMediaId,
    string PickupLocation,
    int Quantity,
    DateOnly StartDate,
    DateOnly EndDate,
    int RentalDays,
    decimal DailyRate,
    decimal DepositPerUnit,
    decimal RentalAmount,
    decimal DepositAmount,
    RentalItemStatus Status);

/// <summary>
/// Một đơn trong danh sách đơn của người thuê.
/// </summary>
/// <param name="OrderId">Mã định danh đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị, ví dụ DH-2026-000001.</param>
/// <param name="Status">Trạng thái đơn.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="TotalRentalAmount">Tổng tiền thuê.</param>
/// <param name="TotalDepositAmount">Tổng tiền cọc.</param>
/// <param name="TotalAmount">Tổng tiền phải trả = tiền thuê + tiền cọc.</param>
/// <param name="ItemCount">Số dòng thiết bị.</param>
/// <param name="SubOrderCount">Số đơn con (số nhà cung cấp).</param>
/// <param name="PaymentDeadline">Hạn thanh toán.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
public sealed record RenterOrderSummaryResponse(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    int ItemCount,
    int SubOrderCount,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset CreatedAt);

/// <summary>
/// Một đơn con trong chi tiết đơn của người thuê.
/// </summary>
/// <param name="SubOrderId">Mã định danh đơn con.</param>
/// <param name="SubOrderCode">Mã đơn con hiển thị, ví dụ DH-2026-000001-01.</param>
/// <param name="SupplierId">Nhà cung cấp của đơn con.</param>
/// <param name="Status">Trạng thái đơn con, tính từ các dòng.</param>
/// <param name="TotalRentalAmount">Tiền thuê của đơn con.</param>
/// <param name="TotalDepositAmount">Tiền cọc của đơn con.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc của đơn con.</param>
/// <param name="Items">Các dòng thiết bị của đơn con.</param>
public sealed record RenterSubOrderResponse(
    Guid SubOrderId,
    string SubOrderCode,
    Guid SupplierId,
    RentalItemStatus Status,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<OrderItemResponse> Items);

/// <summary>
/// Chi tiết đơn cha mà người thuê xem: toàn bộ đơn con và thiết bị.
/// </summary>
/// <param name="OrderId">Mã định danh đơn.</param>
/// <param name="OrderCode">Mã đơn hiển thị.</param>
/// <param name="Status">Trạng thái đơn.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
/// <param name="PaymentDeadline">Hạn thanh toán.</param>
/// <param name="InvoiceRef">Mã hóa đơn bên PaymentService, rỗng khi chưa tạo được hóa đơn.</param>
/// <param name="PaidAt">Thời điểm thanh toán, rỗng khi chưa thanh toán.</param>
/// <param name="Delivery">Thông tin nhận máy.</param>
/// <param name="TotalRentalAmount">Tổng tiền thuê.</param>
/// <param name="TotalDepositAmount">Tổng tiền cọc.</param>
/// <param name="TotalAmount">Tổng tiền phải trả.</param>
/// <param name="SubOrders">Các đơn con, mỗi nhà cung cấp một đơn.</param>
public sealed record RenterOrderDetailResponse(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset PaymentDeadline,
    string? InvoiceRef,
    DateTimeOffset? PaidAt,
    OrderDeliveryResponse Delivery,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<RenterSubOrderResponse> SubOrders);

/// <summary>
/// Một đơn con trong danh sách của nhà cung cấp.
/// </summary>
/// <param name="SubOrderId">Mã định danh đơn con.</param>
/// <param name="SubOrderCode">Mã đơn con hiển thị.</param>
/// <param name="OrderCode">Mã đơn cha, dùng khi trao đổi với người thuê.</param>
/// <param name="Status">Trạng thái đơn con, tính từ các dòng.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="TotalRentalAmount">Tiền thuê phần của nhà cung cấp.</param>
/// <param name="TotalDepositAmount">Tiền cọc phần của nhà cung cấp.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc phần của nhà cung cấp.</param>
/// <param name="ItemCount">Số dòng thiết bị.</param>
/// <param name="PaidAt">Thời điểm người thuê thanh toán.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
public sealed record SupplierSubOrderSummaryResponse(
    Guid SubOrderId,
    string SubOrderCode,
    string OrderCode,
    RentalItemStatus Status,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    int ItemCount,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Chi tiết đơn con mà nhà cung cấp xem: chỉ thiết bị của mình, không có tổng tiền cả đơn hay tài khoản hoàn tiền.
/// </summary>
/// <param name="SubOrderId">Mã định danh đơn con.</param>
/// <param name="SubOrderCode">Mã đơn con hiển thị.</param>
/// <param name="OrderCode">Mã đơn cha.</param>
/// <param name="Status">Trạng thái đơn con, tính từ các dòng.</param>
/// <param name="Currency">Đơn vị tiền.</param>
/// <param name="PaidAt">Thời điểm người thuê thanh toán.</param>
/// <param name="CreatedAt">Thời điểm đặt hàng.</param>
/// <param name="Delivery">Thông tin nhận máy của người thuê.</param>
/// <param name="TotalRentalAmount">Tiền thuê phần của nhà cung cấp.</param>
/// <param name="TotalDepositAmount">Tiền cọc phần của nhà cung cấp.</param>
/// <param name="TotalAmount">Tiền thuê + tiền cọc phần của nhà cung cấp.</param>
/// <param name="Items">Các dòng thiết bị của nhà cung cấp trong đơn.</param>
public sealed record SupplierSubOrderDetailResponse(
    Guid SubOrderId,
    string SubOrderCode,
    string OrderCode,
    RentalItemStatus Status,
    string Currency,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt,
    OrderDeliveryResponse Delivery,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    decimal TotalAmount,
    IReadOnlyList<OrderItemResponse> Items);
