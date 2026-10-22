using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Một trang kết quả truy vấn kèm tổng số bản ghi khớp điều kiện.
/// </summary>
public sealed record RecordPage<T>(IReadOnlyList<T> Items, int TotalCount);

/// <summary>
/// Projection thông tin cơ bản nhận biết một dòng thiết bị trong danh sách đơn hàng.
/// </summary>
public sealed record OrderItemSummaryRecord(
    Guid RentalItemId,
    Guid EquipmentListingId,
    Guid SupplierId,
    string Name,
    Guid? ThumbnailMediaId,
    int Quantity,
    decimal DailyRate,
    decimal RentalAmount,
    decimal DepositAmount,
    DateOnly StartDate,
    DateOnly EndDate,
    RentalItemStatus Status);

/// <summary>
/// Projection một đơn hàng trong danh sách đơn của người thuê.
/// </summary>
public sealed record RenterOrderSummaryRecord(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    int ItemCount,
    int SubOrderCount,
    DateTimeOffset PaymentDeadline,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderItemSummaryRecord> Items)
{
    public RenterOrderSummaryRecord(
        Guid orderId,
        string orderCode,
        RentalOrderStatus status,
        string currency,
        decimal totalRentalAmount,
        decimal totalDepositAmount,
        int itemCount,
        int subOrderCount,
        DateTimeOffset paymentDeadline,
        DateTimeOffset createdAt)
        : this(orderId, orderCode, status, currency, totalRentalAmount, totalDepositAmount, itemCount, subOrderCount, paymentDeadline, createdAt, [])
    {
    }
}


/// <summary>
/// Projection một đơn con trong danh sách đơn của nhà cung cấp; chỉ gồm các dòng của chính đơn con đó.
/// </summary>
public sealed record SupplierSubOrderSummaryRecord(
    Guid SubOrderId,
    string SubOrderCode,
    string OrderCode,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    IReadOnlyList<RentalItemStatus> ItemStatuses,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Điều kiện tìm đơn của Admin; ô rỗng là không lọc. Các điều kiện cấp dòng (dòng, thiết bị, máy, nhà cung cấp,
/// trạng thái dòng) phải cùng khớp trên một dòng.
/// </summary>
public sealed record AdminOrderSearchCriteria(
    string? OrderCode,
    string? SubOrderCode,
    Guid? RentalItemId,
    Guid? ListingId,
    Guid? AssetId,
    Guid? SupplierId,
    Guid? RenterId,
    RentalOrderStatus? Status,
    RentalItemStatus? ItemStatus,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo);

/// <summary>
/// Projection một đơn trong kết quả tìm kiếm của Admin.
/// </summary>
public sealed record AdminOrderSummaryRecord(
    Guid OrderId,
    string OrderCode,
    RentalOrderStatus Status,
    Guid RenterId,
    IReadOnlyList<Guid> SupplierIds,
    string Currency,
    decimal TotalRentalAmount,
    decimal TotalDepositAmount,
    int ItemCount,
    string? InvoiceRef,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderItemSummaryRecord> Items)
{
    public AdminOrderSummaryRecord(
        Guid orderId,
        string orderCode,
        RentalOrderStatus status,
        Guid renterId,
        IReadOnlyList<Guid> supplierIds,
        string currency,
        decimal totalRentalAmount,
        decimal totalDepositAmount,
        int itemCount,
        string? invoiceRef,
        DateTimeOffset? paidAt,
        DateTimeOffset createdAt)
        : this(orderId, orderCode, status, renterId, supplierIds, currency, totalRentalAmount, totalDepositAmount, itemCount, invoiceRef, paidAt, createdAt, [])
    {
    }
}
